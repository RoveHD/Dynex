using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dynex.Desktop;
using Dynex.Shaper;

internal static class NoGamePriorityChecks
{
    internal static async Task Run(DynexRuntime runtime, CaptureFixture fixture, Action<bool, string> check)
    {
        // Only bind/connect local sockets for actual OS process ownership.
        // All constructed media/voice packets go into the fake device.
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var local = new IPEndPoint(IPAddress.Parse("192.168.9.2"), ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
        var remote = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 44444);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        ushort sequence = 0;
        while (runtime.Snapshot?.VoicePriorityPackets is not > 0)
        {
            var audio = new byte[172]; audio[0] = 0x80;
            BinaryPrimitives.WriteUInt16BigEndian(audio.AsSpan(2), sequence);
            BinaryPrimitives.WriteUInt32BigEndian(audio.AsSpan(4), (uint)sequence * 160);
            BinaryPrimitives.WriteUInt32BigEndian(audio.AsSpan(8), 12345);
            sequence++;
            fixture.Inject(Packet(local, remote, PacketDirection.Outbound, false, audio));
            await Task.Delay(25, timeout.Token);
        }
        check(runtime.Snapshot is { IsGaming: false, QosActive: true, VoicePriorityPackets: > 0 },
            "no-game general session marks recognized RTP voice through actual helper ownership/status");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var accepting = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var accepted = await accepting;
        local = (IPEndPoint)client.Client.LocalEndPoint!;
        remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var headers = Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\nContent-Type: video/mp4\r\n\r\n");
        while (runtime.Snapshot?.VideoPriorityPackets is not > 0)
        {
            fixture.Inject(Packet(local, remote, PacketDirection.Inbound, true, headers));
            fixture.Inject(Packet(local, remote, PacketDirection.Outbound, true, []));
            await Task.Delay(50, timeout.Token);
        }
        check(runtime.Snapshot is { IsGaming: false, QosActive: true, VideoPriorityPackets: > 0 },
            "no-game general session classifies media and forwards AF41 ACKs through helper and desktop video counter");
    }

    private static CapturedPacket Packet(IPEndPoint local, IPEndPoint remote, PacketDirection direction, bool tcp, byte[] payload)
    {
        var length = 20 + (tcp ? 20 : 8) + payload.Length;
        var bytes = ArrayPool<byte>.Shared.Rent(length); bytes.AsSpan(0, length).Clear();
        bytes[0] = 0x45; bytes[9] = tcp ? (byte)6 : (byte)17;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)length);
        (direction == PacketDirection.Outbound ? local.Address : remote.Address).GetAddressBytes().CopyTo(bytes, 12);
        (direction == PacketDirection.Outbound ? remote.Address : local.Address).GetAddressBytes().CopyTo(bytes, 16);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), (ushort)(direction == PacketDirection.Outbound ? local.Port : remote.Port));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), (ushort)(direction == PacketDirection.Outbound ? remote.Port : local.Port));
        if (tcp) bytes[32] = 0x50;
        else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), (ushort)(payload.Length + 8));
        payload.CopyTo(bytes, tcp ? 40 : 28);
        return new(bytes, length) { Direction = direction };
    }
}
