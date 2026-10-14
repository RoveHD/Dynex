using System.Buffers;
using System.ComponentModel;

namespace Dynex.Shaper;

internal sealed class WinDivertDevice : IPacketDevice
{
    private nint _handle;

    public WinDivertDevice(string filter)
    {
        _handle = Native.WinDivertOpen(filter, 0, 0, 0);
        if (_handle == -1) { _handle = 0; throw Native.Error("WinDivertOpen fehlgeschlagen"); }
        try
        {
            // Keep kernel and managed backlogs bounded. Default fragment behavior
            // captures reassembled inbound packets (including IPv6 extensions).
            if (!Native.WinDivertSetParam(_handle, 0, 512) ||
                !Native.WinDivertSetParam(_handle, 1, 300) ||
                !Native.WinDivertSetParam(_handle, 2, 2 * 1024 * 1024))
                throw Native.Error("WinDivert-Pufferkonfiguration fehlgeschlagen");
        }
        catch { Dispose(); throw; }
    }

    public CapturedPacket Receive()
    {
        var bytes = ArrayPool<byte>.Shared.Rent(65575);
        try
        {
            var handle = Volatile.Read(ref _handle);
            if (handle == 0) throw new ObjectDisposedException(nameof(WinDivertDevice));
            if (!Native.WinDivertRecv(handle, bytes, 65575, out var length, out var address))
                throw Native.Error("WinDivertRecv fehlgeschlagen");
            return new CapturedPacket(bytes, checked((int)length)) { Address = address };
        }
        catch { ArrayPool<byte>.Shared.Return(bytes); throw; }
    }

    public void Send(CapturedPacket packet)
    {
        var handle = Volatile.Read(ref _handle);
        if (handle == 0) throw new ObjectDisposedException(nameof(WinDivertDevice));
        // WinDivert 2.2 API 5.7 explicitly permits reinjecting an unmodified
        // Recv packet/address pair, including unset offload checksum flags.
        // Preserve both packet bytes and Address; no checksum rewrite is needed.
        if (!Native.WinDivertSend(handle, packet.Buffer, (uint)packet.Length, out var sent, ref packet.Address))
            throw Native.Error("WinDivertSend fehlgeschlagen");
        if (sent != packet.Length) throw new Win32Exception("WinDivertSend hat ein unvollständiges Paket gesendet.");
    }

    public void Dispose()
    {
        // No SafeHandle ref-count on the blocking Recv: closing must interrupt it.
        // A session is fully joined before a new handle may be opened.
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle == 0) return;
        Native.WinDivertShutdown(handle, 3);
        Native.WinDivertClose(handle);
    }
}
