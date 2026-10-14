using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Dynex.Shaper;

/// <summary>
/// Standard-MTU x64 Ethernet backend for the separately installed Windows Packet
/// Filter 3.6.2 driver. Uses the official IOCTL ABI, not WinDivert or ndisapi.dll.
/// Only one Receive caller is supported; Send and Dispose are serialized safely.
/// </summary>
public sealed unsafe class NdisPacketDevice : IPacketDevice
{
    private const uint ExpectedDriverVersion = 0x06023000; // v3.6.2 Common.h NDISRD_VERSION
    private const int MaxAdapters = 32, NamesOffset = 4, HandlesOffset = 8196;
    private const int MediumOffset = 8452, MtuOffset = 8772;
    private const int HeaderSize = 52, FrameCapacity = 1514, NativeFrameSize = HeaderSize + FrameCapacity;
    private const int AdapterListSize = 8836;
    private readonly uint _captureMode;
    private readonly object _gate = new();
    private readonly ManualResetEvent _stopped = new(false);
    private readonly List<Adapter> _adapters = [];
    private SafeFileHandle? _driver;
    private Semaphore? _exclusive;
    private bool _exclusiveOwned;
    private nint _receiveBuffer, _sendBuffer;
    private int _closed, _nextAdapter;
    private bool _receiving, _cleanupDone, _notificationsDisposed;
    private readonly IoControl? _ioOverride;

    // Injectable transport for isolated native-lifecycle tests; production always
    // calls DeviceIoControl on the real, version-checked driver handle.
    internal unsafe delegate uint IoControl(int index, void* input, int inputLength, void* output, int outputLength);
    internal NdisPacketDevice(IoControl io, params nint[] capturedAdapters)
    {
        ValidateAbi();
        _ioOverride = io;
        _captureMode = 3;
        foreach (var handle in capturedAdapters)
            _adapters.Add(new Adapter(handle) { Owned = true, EventAttached = true, CaptureStarted = true });
    }

    public uint DriverVersion { get; }
    public string? CleanupError { get; private set; }

    public NdisPacketDevice(bool captureOutbound = false)
    {
        _captureMode = captureOutbound ? 3u : 2u;
        try
        {
            ValidateAbi();
            _exclusive = new Semaphore(1, 1, @"Global\Dynex.NdisPacketDevice");
            if (!_exclusive.WaitOne(0)) throw new InvalidOperationException("Ein anderer DYNEX-Netzwerkhelfer ist bereits aktiv.");
            _exclusiveOwned = true;
            // Read-only registry inspection: unlike the upstream C++ helper's
            // RegCreateKey, this never creates or changes driver configuration.
            using (var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ndisrd\Parameters", writable: false))
            {
                var startup = parameters?.GetValue("StartupMode");
                if (startup is not null && (startup is not int flags || flags != 0))
                    throw new InvalidOperationException("NDISRD hat einen fremden Startfiltermodus. DYNEX verändert ihn nicht.");
            }
            _driver = OpenDriver();
            uint version = uint.MaxValue;
            Io(0, &version, 4, &version, 4);
            DriverVersion = version;
            // Deliberately version-bound until a newer driver's ABI is reviewed.
            if (version != ExpectedDriverVersion)
                throw new NotSupportedException($"Windows Packet Filter 3.6.2 wird benötigt (API 0x{version:X8}).");
            uint filters = 0;
            Io(16, null, 0, &filters, 4);
            if (filters != 0) throw new InvalidOperationException("NDISRD wird bereits durch fremde Paketfilter verwendet.");

            var liveIds = ActiveEthernetInterfaces();
            var list = new byte[AdapterListSize];
            fixed (byte* buffer = list) Io(1, buffer, AdapterListSize, buffer, AdapterListSize);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(list);
            if (count > MaxAdapters) throw new InvalidDataException("NDISRD meldet eine ungültige Adapterliste.");
            for (var i = 0; i < count; i++)
            {
                var handle = (nint)BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan(HandlesOffset + i * 8));
                if (handle == 0) continue;
                // Validate every existing adapter mode before mutating any mode.
                // Shared standard-driver modes/filter tables are not per handle.
                if (Mode(handle) != 0) throw new InvalidOperationException("Ein Netzwerkadapter wird bereits durch einen anderen Paketfilter verwendet.");
                var nameBytes = list.AsSpan(NamesOffset + i * 256, 256);
                var terminator = nameBytes.IndexOf((byte)0);
                if (terminator < 0) throw new InvalidDataException("Ungültiger NDIS-Adaptername.");
                var name = Encoding.ASCII.GetString(nameBytes[..terminator]);
                var start = name.IndexOf('{'); var end = name.IndexOf('}');
                if (start < 0 || end <= start || !Guid.TryParse(name[start..(end + 1)], out var id) || !liveIds.Contains(id)) continue;
                // NdisMedium802_3 only: never reinterpret raw-IP, WAN or native
                // 802.11 frames as Ethernet or reinject them with a guessed header.
                if (BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(MediumOffset + i * 4)) != 0) continue;
                var mtu = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(MtuOffset + i * 2));
                if (mtu is 0 or > 1500) throw new NotSupportedException("Dieser NDIS-Backend unterstützt ausschließlich Ethernet-MTU bis 1500 Byte.");
                _adapters.Add(new Adapter(handle));
            }
            if (_adapters.Count == 0) throw new NotSupportedException("Kein unterstützter aktiver Ethernet-/WLAN-Adapter mit Internetgateway gefunden.");
            _receiveBuffer = (nint)NativeMemory.AllocZeroed(NativeFrameSize);
            _sendBuffer = (nint)NativeMemory.AllocZeroed(NativeFrameSize);
            if (_receiveBuffer == 0 || _sendBuffer == 0) throw new OutOfMemoryException();
            foreach (var adapter in _adapters)
            {
                // Recheck immediately before mutation; competing clients cannot
                // be made safe by the DYNEX-only semaphore alone.
                if (Mode(adapter.Handle) != 0) throw new InvalidOperationException("Ein anderer Paketfilter hat den Netzwerkadapter übernommen.");
                SetEvent(adapter.Handle, adapter.Event.SafeWaitHandle.DangerousGetHandle());
                adapter.EventAttached = true;
                SetMode(adapter.Handle, _captureMode);
                adapter.Owned = true;
            }
        }
        catch (Exception setupError)
        {
            Dispose();
            if (CleanupError is { } cleanupError)
                throw new PacketReleaseException("Freigabe nach fehlgeschlagener Einrichtung nicht bestätigt: " + cleanupError, setupError);
            throw;
        }
    }

    public CapturedPacket Receive()
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (_receiving) throw new InvalidOperationException("Gleichzeitige NDIS-Receive-Aufrufe werden nicht unterstützt.");
            _receiving = true;
        }
        try { return ReceiveCore(); }
        finally
        {
            lock (_gate)
            {
                _receiving = false;
                if (_cleanupDone) DisposeNotifications();
            }
        }
    }

    private CapturedPacket ReceiveCore()
    {
        var waits = new WaitHandle[_adapters.Count + 1];
        waits[0] = _stopped;
        for (var i = 0; i < _adapters.Count; i++) waits[i + 1] = _adapters[i].Event;
        while (true)
        {
            lock (_gate)
            {
                ThrowIfClosed();
                for (var turn = 0; turn < _adapters.Count; turn++)
                {
                    var index = (_nextAdapter + turn) % _adapters.Count;
                    var adapter = _adapters[index];
                    // Reset before checking queue, so an arrival between this
                    // check and WaitAny still wakes the receiver.
                    adapter.Event.Reset();
                    var handle = adapter.Handle;
                    uint queued = 0;
                    Io(12, &handle, 8, &queued, 4);
                    if (queued == 0) continue;
                    if (Mode(handle) != _captureMode)
                        throw new IOException("Ein anderer Client hat den NDIS-Empfangsmodus verändert.");
                    var request = new EthRequest { Adapter = handle, Buffer = _receiveBuffer };
                    Io(4, &request, 16, &request, 16);
                    var header = (FrameHeader*)_receiveBuffer;
                    if ((header->DeviceFlags != 2 && !(header->DeviceFlags == 1 && _captureMode == 3)) || header->Length is < 14 or > FrameCapacity)
                        throw new InvalidDataException("NDISRD hat keinen unterstützten Ethernet-Frame geliefert.");
                    var bytes = ArrayPool<byte>.Shared.Rent((int)header->Length);
                    try
                    {
                        new ReadOnlySpan<byte>((byte*)_receiveBuffer + HeaderSize, (int)header->Length).CopyTo(bytes);
                        var packet = new CapturedPacket(bytes, (int)header->Length)
                        {
                            IpOffset = GetIpOffset(bytes.AsSpan(0, (int)header->Length)),
                            Direction = header->DeviceFlags == 1 ? PacketDirection.Outbound : PacketDirection.Inbound,
                            BackendMetadata = new PacketMetadata(this, handle,
                                new ReadOnlySpan<byte>((void*)_receiveBuffer, HeaderSize).ToArray())
                        };
                        _nextAdapter = (index + 1) % _adapters.Count;
                        return packet;
                    }
                    catch { ArrayPool<byte>.Shared.Return(bytes); throw; }
                }
            }
            // Events are the notification mechanism. The bounded timeout also
            // permits liveness/adapter-mode checks without a spinning poll loop.
            if (WaitHandle.WaitAny(waits, 1000) == 0) throw new ObjectDisposedException(nameof(NdisPacketDevice));
            lock (_gate)
            {
                ThrowIfClosed();
                foreach (var adapter in _adapters)
                    if (Mode(adapter.Handle) != _captureMode)
                        throw new IOException("Der NDIS-Adaptermodus wurde außerhalb von DYNEX verändert.");
            }
        }
    }

    public void Send(CapturedPacket packet)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (packet.BackendMetadata is not PacketMetadata metadata || !ReferenceEquals(metadata.Owner, this) ||
                packet.Length is < 14 or > FrameCapacity)
                throw new ArgumentException("Der Frame gehört nicht zu dieser NDIS-Sitzung.", nameof(packet));
            metadata.Header.CopyTo(new Span<byte>((void*)_sendBuffer, HeaderSize));
            packet.Buffer.AsSpan(0, packet.Length).CopyTo(new Span<byte>((byte*)_sendBuffer + HeaderSize, packet.Length));
            var request = new EthRequest { Adapter = metadata.Adapter, Buffer = _sendBuffer };
            // Preserve Ethernet/VLAN and original NDIS metadata. IP-only DSCP
            // edits happen in the QoS policy; reinjection keeps native direction.
            // Direction comes from the original native header, never a guessed
            // IP address or a mutable classifier decision.
            Io(GetSendIoctlIndex(BinaryPrimitives.ReadUInt32LittleEndian(metadata.Header.AsSpan(16))), &request, 16, null, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        lock (_gate)
        {
            _stopped.Set();
            if (_ioOverride is not null || _driver is { IsInvalid: false, IsClosed: false })
            {
                foreach (var adapter in _adapters)
                {
                    try
                    {
                        if (adapter.Owned)
                        {
                            if (Mode(adapter.Handle) != _captureMode)
                                throw new IOException("Fremder Adaptermodus wird bei Abschaltung nicht überschrieben.");
                            SetMode(adapter.Handle, 0);
                            adapter.Owned = false;
                        }
                        if (adapter.EventAttached) { SetEvent(adapter.Handle, 0); adapter.EventAttached = false; }
                        // Queue flushing only follows a successfully released own
                        // capture mode; do not flush a foreign client's queue.
                        if (adapter.CaptureStarted)
                        {
                            var handle = adapter.Handle;
                            Io(6, &handle, 8, null, 0);
                        }
                    }
                    catch (Win32Exception e) when (e.NativeErrorCode == 87)
                    {
                        // A driver rebind can invalidate a previously captured
                        // adapter handle. Never send reset/event/flush commands to
                        // its replacement, and never equate ERROR_INVALID_PARAMETER
                        // alone with successful release. Only the driver's fresh
                        // complete handle inventory can establish that it is gone.
                        try
                        {
                            if (AdapterStillPresent(adapter.Handle)) CleanupError ??= e.Message;
                        }
                        catch (Exception verificationError)
                        {
                            CleanupError ??= e.Message + " Adapterwechsel konnte nicht bestätigt werden: " + verificationError.Message;
                        }
                    }
                    catch (Exception e) { CleanupError ??= e.Message; }
                }
            }
            _driver?.Dispose(); _driver = null;
            NativeMemory.Free((void*)_receiveBuffer); _receiveBuffer = 0;
            NativeMemory.Free((void*)_sendBuffer); _sendBuffer = 0;
            if (_exclusiveOwned) { _exclusive!.Release(); _exclusiveOwned = false; }
            _exclusive?.Dispose(); _exclusive = null;
            _cleanupDone = true;
            if (!_receiving) DisposeNotifications();
        }
    }

    private void DisposeNotifications()
    {
        if (_notificationsDisposed) return;
        _notificationsDisposed = true;
        foreach (var adapter in _adapters) adapter.Event.Dispose();
        _stopped.Dispose();
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

    private uint Mode(nint adapter)
    {
        var mode = new AdapterMode { Adapter = adapter };
        Io(13, &mode, 12, &mode, 12);
        return mode.Flags;
    }
    private void SetMode(nint adapter, uint flags)
    {
        var mode = new AdapterMode { Adapter = adapter, Flags = flags };
        Io(5, &mode, 12, null, 0);
        if (flags == _captureMode) _adapters.First(a => a.Handle == adapter).CaptureStarted = true;
    }
    private void SetEvent(nint adapter, nint value)
    {
        var notification = new AdapterEvent { Adapter = adapter, Event = value };
        Io(7, &notification, 16, null, 0);
    }
    private bool AdapterStillPresent(nint handle)
    {
        var list = new byte[AdapterListSize];
        uint returned;
        fixed (byte* buffer = list) returned = Io(1, buffer, AdapterListSize, buffer, AdapterListSize);
        return AdapterListContains(list, returned, handle);
    }

    internal static bool AdapterListContains(ReadOnlySpan<byte> list, uint returned, nint handle)
    {
        if (handle == 0 || returned < 4 || returned > list.Length)
            throw new InvalidDataException("Unvollständige NDIS-Adapterliste.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(list);
        if (count > MaxAdapters || count > 0 && returned < HandlesOffset + count * 8)
            throw new InvalidDataException("Unvollständige oder ungültige NDIS-Adapterhandles.");
        var found = false;
        for (var i = 0; i < count; i++)
        {
            var candidate = (nint)BinaryPrimitives.ReadInt64LittleEndian(list.Slice(HandlesOffset + i * 8, 8));
            if (candidate == 0) throw new InvalidDataException("Ungültiges leeres NDIS-Adapterhandle.");
            found |= candidate == handle;
        }
        return found;
    }

    private uint Io(int index, void* input, int inputLength, void* output, int outputLength)
    {
        if (_ioOverride is not null) return _ioOverride(index, input, inputLength, output, outputLength);
        // Official Common.h: CTL_CODE(0x8300, 0x830 + index, METHOD_BUFFERED, FILE_ANY_ACCESS).
        var code = 0x83000000u | ((0x830u + (uint)index) << 2);
        ObjectDisposedException.ThrowIf(_driver is null, this);
        if (!DeviceIoControl(_driver, code, input, (uint)inputLength, output, (uint)outputLength, out var returned, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, $"NDISRD IOCTL {index}: {new Win32Exception(error).Message}");
        }
        return returned;
    }

    private static SafeFileHandle OpenDriver()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Windows x64 wird benötigt.");
        var driver = CreateFile(@"\\.\NDISRD", 0, 3, 0, 3, 0, 0);
        if (!driver.IsInvalid) return driver;
        var error = Marshal.GetLastPInvokeError(); driver.Dispose();
        throw new Win32Exception(error, "Windows Packet Filter ist nicht installiert, nicht gestartet oder nicht zugänglich.");
    }

    private static HashSet<Guid> ActiveEthernetInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            (n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211) &&
            n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
        .Select(n => Guid.TryParse(n.Id, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();

    /// <summary>Pure Ethernet/VLAN framing check; -1 means immediate bypass.</summary>
    public static int GetSendIoctlIndex(uint originalDeviceFlags) => originalDeviceFlags switch
    {
        1 => 2, // SEND_PACKET_TO_ADAPTER: outbound
        2 => 3, // SEND_PACKET_TO_MSTCP: inbound
        _ => throw new InvalidDataException("Ungültige ursprüngliche NDIS-Paketrichtung.")
    };

    public static int GetIpOffset(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14) return -1;
        var offset = 14;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        for (var tags = 0; etherType is 0x8100 or 0x88A8; tags++)
        {
            if (tags == 2 || frame.Length < offset + 4) return -1;
            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset + 2, 2));
            offset += 4;
        }
        if (etherType == 0x0800 && frame.Length >= offset + 20 && frame[offset] >> 4 == 4) return offset;
        if (etherType == 0x86DD && frame.Length >= offset + 40 && frame[offset] >> 4 == 6) return offset;
        return -1;
    }

    public static (int Mode, int Request, int Header, int Frame, int AdapterList) GetAbiSizes() =>
        (sizeof(AdapterMode), sizeof(EthRequest), sizeof(FrameHeader), sizeof(NativeFrame), sizeof(AdapterList));

    public static void ValidateAbi()
    {
        if (IntPtr.Size != 8 || GetAbiSizes() != (12, 16, 52, 1566, 8836) ||
            Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Length)).ToInt32() != 20)
            throw new PlatformNotSupportedException("Die NDISRD-x64-Strukturgrößen stimmen nicht überein.");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct AdapterMode { public nint Adapter; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct AdapterEvent { public nint Adapter, Event; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct EthRequest { public nint Adapter, Buffer; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct FrameHeader
    {
        public nint LinkForward, LinkBackward;
        public uint DeviceFlags, Length, Flags, Vlan, FilterId;
        public fixed uint Reserved[4];
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct NativeFrame { public FrameHeader Header; public fixed byte Data[FrameCapacity]; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct AdapterList
    {
        public uint Count; public fixed byte Names[8192]; public fixed ulong Handles[32];
        public fixed uint Medium[32]; public fixed byte Mac[192]; public fixed ushort Mtu[32];
    }
    private sealed class Adapter(nint handle)
    {
        internal nint Handle { get; } = handle;
        internal ManualResetEvent Event { get; } = new(false);
        internal bool EventAttached, Owned, CaptureStarted;
    }
    private sealed record PacketMetadata(NdisPacketDevice Owner, nint Adapter, byte[] Header);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, void* input, uint inputLength,
        void* output, uint outputLength, out uint returned, nint overlapped);
}
