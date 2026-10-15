using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Dynex.Shaper;

public enum TransportProtocol : byte { Tcp = 6, Udp = 17 }

/// <summary>Inbound orientation: destination is local, source is remote. No Ethernet header.</summary>
public readonly record struct SocketFlow(TransportProtocol Protocol, IPAddress LocalAddress, ushort LocalPort,
    IPAddress RemoteAddress, ushort RemotePort);

public static class InboundPacketParser
{
    /// <summary>Unknown/truncated/fragmented/encrypted transports return false; callers must bypass shaping.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out SocketFlow flow)
        => TryParse(packet, out flow, out _);

    public static bool TryParse(ReadOnlySpan<byte> packet, out SocketFlow flow, out int transportPayloadLength)
        => TryParse(packet, out flow, out transportPayloadLength, out _);

    public static bool TryParse(ReadOnlySpan<byte> packet, out SocketFlow flow, out int transportPayloadLength, out int transportPayloadOffset)
    {
        flow = default;
        transportPayloadLength = 0;
        transportPayloadOffset = 0;
        if (packet.IsEmpty) return false;
        int offset, end;
        byte next;
        IPAddress source, destination;
        if (packet[0] >> 4 == 4)
        {
            if (packet.Length < 20) return false;
            offset = (packet[0] & 15) * 4;
            end = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
            var fragment = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
            // DF is harmless. Reserved flag, MF or any fragment offset is not classifiable.
            if (offset < 20 || end < offset || end > packet.Length || (fragment & 0xBFFF) != 0) return false;
            next = packet[9];
            source = new(packet.Slice(12, 4)); destination = new(packet.Slice(16, 4));
        }
        else if (packet[0] >> 4 == 6)
        {
            if (packet.Length < 40) return false;
            var payload = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
            // IPv6 jumbograms need Hop-by-Hop Jumbo Payload interpretation; fail open here.
            if (payload == 0 || payload + 40 > packet.Length) return false;
            end = payload + 40; offset = 40; next = packet[6];
            source = new(packet.Slice(8, 16)); destination = new(packet.Slice(24, 16));
            for (var headers = 0; next is not (6 or 17); headers++)
            {
                if (headers >= 16 || next is 44 or 50 or 59 || offset + 2 > end) return false;
                int length;
                if (next is 0 or 43 or 60)
                {
                    length = (packet[offset + 1] + 1) * 8;
                    // An active routing header has not reached the final socket destination.
                    if (next == 43 && (offset + 4 > end || packet[offset + 3] != 0)) return false;
                }
                else if (next == 51)
                {
                    length = (packet[offset + 1] + 2) * 4;
                    if (length < 12) return false;
                }
                else return false;
                if (length > end - offset) return false;
                next = packet[offset]; offset += length;
            }
        }
        else return false;

        if (next == 6)
        {
            if (end - offset < 20) return false;
            var tcpHeader = (packet[offset + 12] >> 4) * 4;
            if (tcpHeader < 20 || tcpHeader > end - offset) return false;
            transportPayloadLength = end - offset - tcpHeader;
            transportPayloadOffset = offset + tcpHeader;
        }
        else if (next == 17)
        {
            if (end - offset < 8) return false;
            var udpLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 4, 2));
            if (udpLength < 8 || udpLength > end - offset) return false;
            transportPayloadLength = udpLength - 8;
            transportPayloadOffset = offset + 8;
        }
        else return false;
        var remotePort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
        var localPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
        if (localPort == 0 || remotePort == 0) return false;
        flow = new((TransportProtocol)next, destination, localPort, source, remotePort);
        return true;
    }
}

/// <summary>
/// Compare BOTH PID and ProcessStartedAtUtc with a protected game's identity. A PID alone can be reused.
/// A null creation time is an explicit limitation (exited/inaccessible process), not identity evidence.
/// </summary>
public sealed record TrafficOwner(int ProcessId, DateTimeOffset? ProcessStartedAtUtc, string? ProcessName = null);
public sealed record OwnershipEntry(SocketFlow Flow, int ProcessId, DateTimeOffset? ProcessStartedAtUtc = null, string? ProcessName = null);

/// <summary>
/// Immutable table view. Classify returns null for stale (&gt;1 s), missing, incomplete or ambiguous ownership.
/// UDP tables expose local binds only, not per-datagram remote ownership; all matching exact and wildcard
/// owners must agree. Scope IDs absent from packet headers are not treated as proof of an interface.
/// </summary>
public sealed class OwnershipSnapshot
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(1);
    private readonly Dictionary<FlowKey, TrafficOwner?> _tcp = new();
    private readonly Dictionary<EndpointKey, TrafficOwner?> _udp = new();
    private readonly HashSet<ushort> _ipv6UdpWildcardPorts = [];
    private readonly long _capturedTick;
    private readonly TimeSpan _initialAge;
    public DateTimeOffset CapturedAtUtc { get; }
    public bool IsComplete { get; }
    public TimeSpan Age => _initialAge + Stopwatch.GetElapsedTime(_capturedTick);
    public bool IsFresh => IsComplete && Age <= MaximumAge;
    public int EntryCount { get; }

    internal OwnershipSnapshot(IEnumerable<OwnershipEntry> entries, DateTimeOffset at, long tick, bool complete, TimeSpan initialAge = default)
    {
        CapturedAtUtc = at; _capturedTick = tick; IsComplete = complete; _initialAge = initialAge;
        var count = 0;
        foreach (var entry in entries)
        {
            if (++count > TrafficOwnership.MaximumEntries) throw new InvalidOperationException("Zu viele Socket-Einträge.");
            var flow = entry.Flow;
            var local = EndpointKey.From(flow.LocalAddress, flow.LocalPort);
            var remote = EndpointKey.From(flow.RemoteAddress, flow.RemotePort);
            if (local is null || remote is null || local.Value.Family != remote.Value.Family || flow.LocalPort == 0) continue;
            TrafficOwner? owner = entry.ProcessId > 0 && (entry.ProcessStartedAtUtc is null || entry.ProcessStartedAtUtc <= at)
                ? new(entry.ProcessId, entry.ProcessStartedAtUtc, entry.ProcessName) : null;
            if (flow.Protocol == TransportProtocol.Tcp)
            {
                // A listening wildcard is not an exact connected flow and must never catch arbitrary packets.
                if (flow.RemotePort != 0 && !local.Value.IsAny && !remote.Value.IsAny) Merge(_tcp, new(local.Value, remote.Value), owner);
            }
            else if (flow.Protocol == TransportProtocol.Udp)
            {
                Merge(_udp, local.Value, owner);
                if (local.Value.Family == 6 && local.Value.IsAny) _ipv6UdpWildcardPorts.Add(flow.LocalPort);
            }
        }
        EntryCount = count;
    }

    /// <summary>Factory for deterministic table tests and offline classification. Backdated views expire normally.</summary>
    public static OwnershipSnapshot Create(IEnumerable<OwnershipEntry> entries, DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var now = DateTimeOffset.UtcNow;
        var at = capturedAtUtc ?? now;
        return new(entries, at, Stopwatch.GetTimestamp(), at <= now.AddSeconds(1), at < now ? now - at : TimeSpan.Zero);
    }

    public TrafficOwner? Classify(SocketFlow flow)
    {
        if (!IsFresh) return null;
        var local = EndpointKey.From(flow.LocalAddress, flow.LocalPort);
        var remote = EndpointKey.From(flow.RemoteAddress, flow.RemotePort);
        if (local is null || remote is null || local.Value.Family != remote.Value.Family || flow.LocalPort == 0) return null;
        if (flow.Protocol == TransportProtocol.Tcp) return _tcp.GetValueOrDefault(new(local.Value, remote.Value));
        if (flow.Protocol != TransportProtocol.Udp) return null;
        // IP Helper does not expose IPV6_V6ONLY. A :: wildcard might be a competing dual-mode owner.
        // Treat that cross-family uncertainty as unknown rather than assigning another process's traffic.
        if (local.Value.Family == 4 && _ipv6UdpWildcardPorts.Contains(flow.LocalPort)) return null;
        var hasExact = _udp.TryGetValue(local.Value, out var exact);
        var wildcardKey = local.Value with { High = 0, Low = 0 };
        var hasWildcard = _udp.TryGetValue(wildcardKey, out var wildcard);
        if (hasExact && hasWildcard) return exact is not null && exact == wildcard ? exact : null;
        return hasExact ? exact : hasWildcard ? wildcard : null;
    }

    private static void Merge<TKey>(Dictionary<TKey, TrafficOwner?> target, TKey key, TrafficOwner? owner) where TKey : notnull
    {
        if (!target.TryGetValue(key, out var current)) target.Add(key, owner);
        else if (current != owner) target[key] = null; // Ambiguity is sticky for this immutable snapshot.
    }

    private readonly record struct FlowKey(EndpointKey Local, EndpointKey Remote);
    private readonly record struct EndpointKey(byte Family, ulong High, ulong Low, ushort Port)
    {
        internal bool IsAny => High == 0 && Low == 0;
        internal static EndpointKey? From(IPAddress? address, ushort port)
        {
            if (address is null) return null;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var bytes = address.GetAddressBytes();
            return bytes.Length switch
            {
                4 => new(4, 0, BinaryPrimitives.ReadUInt32BigEndian(bytes), port),
                16 => new(6, BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8)), port),
                _ => null
            };
        }
    }
}

/// <summary>
/// Read-only Windows IP Helper ownership cache. Refresh on a separate worker (for example every 250 ms),
/// never from a packet callback. A failed refresh publishes an empty incomplete view immediately.
/// Native tables are sequential point-in-time observations, not a kernel-atomic ownership guarantee.
/// Closed/new sockets between refreshes and UDP port sharing necessarily remain fail-open limitations.
/// </summary>
public sealed class TrafficOwnership : IDisposable
{
    internal const int MaximumEntries = 131072;
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private OwnershipSnapshot _snapshot = new([], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), false);
    private string? _lastError;
    private bool _disposed;
    private IReadOnlyDictionary<int, Dynex.Native.ProcessTable.Identity> _processTable = new Dictionary<int, Dynex.Native.ProcessTable.Identity>();
    private long _processTableTick;
    public OwnershipSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public string? LastError => Volatile.Read(ref _lastError);

    public bool Refresh()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var at = DateTimeOffset.UtcNow; var tick = Stopwatch.GetTimestamp();
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Socket-Zuordnung benötigt Windows IP Helper.");
                var rows = new List<OwnershipEntry>();
                ReadTable(rows, true, 2); ReadTable(rows, false, 2);
                ReadTable(rows, true, 23); ReadTable(rows, false, 23);
                var identities = new Dictionary<int, (DateTimeOffset? Started, string? Name)>();
                // Handle-free process identities (never opening the game). The table is re-read only when a
                // socket belongs to a PID it does not know yet, and otherwise at most every two seconds;
                // the 250 ms refresh then costs only the IP Helper table reads.
                var table = _processTable;
                if (Stopwatch.GetElapsedTime(_processTableTick, tick) >= TimeSpan.FromSeconds(2) ||
                    rows.Any(row => row.ProcessId > 0 && !table.ContainsKey(row.ProcessId)))
                {
                    table = _processTable = Dynex.Native.ProcessTable.Snapshot();
                    _processTableTick = tick;
                }
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (!identities.TryGetValue(row.ProcessId, out var identity))
                    {
                        if (identities.Count >= 4096) throw new InvalidOperationException("Zu viele unterschiedliche Socket-Prozesse.");
                        identity = table.TryGetValue(row.ProcessId, out var known)
                            ? (known.StartedAtUtc, known.Name.Trim().ToLowerInvariant()) : (null, null);
                        identities[row.ProcessId] = identity;
                    }
                    // Missing process identity cannot establish safe per-program ownership.
                    rows[i] = row with { ProcessId = identity.Started is not null && identity.Name is not null ? row.ProcessId : 0,
                        ProcessStartedAtUtc = identity.Started, ProcessName = identity.Name };
                }
                var snapshot = new OwnershipSnapshot(rows, at, tick, true);
                Volatile.Write(ref _lastError, null);
                Volatile.Write(ref _snapshot, snapshot);
                return true;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException or OverflowException)
            {
                Volatile.Write(ref _lastError, "Socket-Zuordnung nicht verfügbar: " + e.Message);
                Volatile.Write(ref _snapshot, new([], at, tick, false));
                return false;
            }
        }
    }

    private static void ReadTable(List<OwnershipEntry> rows, bool tcp, uint family)
    {
        uint size = 0;
        uint Query(IntPtr memory, ref uint length) => tcp
            ? GetExtendedTcpTable(memory, ref length, false, family, 5, 0) // TCP_TABLE_OWNER_PID_ALL
            : GetExtendedUdpTable(memory, ref length, false, family, 1, 0); // UDP_TABLE_OWNER_PID
        var result = Query(IntPtr.Zero, ref size);
        if (result != 122 && result != 0) throw new Win32Exception((int)result);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < 4 || size > MaximumTableBytes) throw new InvalidOperationException("Ungültige Socket-Tabellengröße.");
            var capacity = size;
            var memory = Marshal.AllocHGlobal(checked((int)capacity));
            try
            {
                result = Query(memory, ref size);
                if (result == 122) continue; // Explicit size race: retry only with the API's larger size.
                if (result != 0) throw new Win32Exception((int)result);
                if (size > capacity || size < 4) throw new InvalidOperationException("Ungültige Socket-Tabellenantwort.");
                var bytes = new byte[size]; Marshal.Copy(memory, bytes, 0, checked((int)size));
                var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                var rowSize = tcp ? family == 2 ? 24 : 56 : family == 2 ? 12 : 28;
                if (count > MaximumEntries - rows.Count || (ulong)count * (uint)rowSize + 4 > size)
                    throw new InvalidOperationException("Socket-Tabelle ist unvollständig oder zu groß.");
                for (var i = 0; i < count; i++) rows.Add(ParseRow(bytes.AsSpan(4 + i * rowSize, rowSize), tcp, family));
                return;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new InvalidOperationException("Socket-Tabelle änderte ihre Größe während der Abfrage.");
    }

    private static OwnershipEntry ParseRow(ReadOnlySpan<byte> row, bool tcp, uint family)
    {
        IPAddress local, remote; ushort localPort, remotePort; uint pid;
        if (family == 2)
        {
            var start = tcp ? 4 : 0;
            local = new(row.Slice(start, 4)); localPort = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(start + 4, 2));
            remote = tcp ? new(row.Slice(12, 4)) : IPAddress.Any;
            remotePort = tcp ? BinaryPrimitives.ReadUInt16BigEndian(row.Slice(16, 2)) : (ushort)0;
            pid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(tcp ? 20 : 8, 4));
        }
        else
        {
            // Scope IDs are deliberately excluded from the key: raw IP packets do not carry zone IDs.
            // Multiple conflicting owners on otherwise identical scoped endpoints become ambiguous.
            local = new(row[..16]); localPort = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(20, 2));
            remote = tcp ? new(row.Slice(24, 16)) : IPAddress.IPv6Any;
            remotePort = tcp ? BinaryPrimitives.ReadUInt16BigEndian(row.Slice(44, 2)) : (ushort)0;
            pid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(tcp ? 52 : 24, 4));
        }
        return new(new(tcp ? TransportProtocol.Tcp : TransportProtocol.Udp, local, localPort, remote, remotePort), pid <= int.MaxValue ? (int)pid : 0);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _snapshot, new([], DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), false));
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
}
