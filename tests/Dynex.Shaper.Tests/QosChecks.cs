using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dynex.Desktop;
using Dynex.Shaper;

internal static class QosChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        check(!ControlProtocol.ParseCommand("{\"mbps\":null}").QosEnabled &&
            ControlProtocol.ParseCommand("{\"mbps\":null,\"qosEnabled\":true}").QosEnabled, "QoS-only IPC is opt-in and legacy defaults off");
        check(NdisPacketDevice.GetSendIoctlIndex(1) == 2 && NdisPacketDevice.GetSendIoctlIndex(2) == 3,
            "original NDIS flags route outbound to adapter and inbound to MSTCP");
        foreach (var ipv6 in new[] { false, true })
        foreach (var priority in new[] { PacketPriority.Game, PacketPriority.Voice, PacketPriority.Video })
        {
            var correct = true;
            for (var ecn = 0; ecn < 4; ecn++)
            for (var tags = 0; tags < 3; tags++)
            {
                var flow = Flow(ipv6);
                using var packet = Packet(flow, PacketDirection.Outbound, new byte[160], tags, ipv6 ? 0 : 12);
                var offset = packet.IpOffset;
                var ip = packet.Buffer.AsSpan(offset, packet.Length - offset);
                if (ipv6) { ip[0] = 0x6a; ip[1] = (byte)((ecn << 4) | 11); }
                else ip[1] = (byte)(0x28 | ecn);
                var before = packet.Buffer.AsSpan(0, packet.Length).ToArray();
                correct &= QosPacketMarker.TryMark(packet, priority);
                var ds = ipv6 ? ((ip[0] & 15) << 4) | (ip[1] >> 4) : ip[1];
                correct &= ds >> 2 == (priority switch { PacketPriority.Game => 32, PacketPriority.Voice => 46, _ => 34 }) && (ds & 3) == ecn;
                var payload = offset + (ipv6 ? 40 : 32);
                correct &= packet.Buffer.AsSpan(payload, packet.Length - payload).SequenceEqual(before.AsSpan(payload));
                correct &= packet.Buffer.AsSpan(0, offset).SequenceEqual(before.AsSpan(0, offset));
                correct &= ipv6 ? (ip[1] & 15) == 11 && ip[2] == before[offset + 2] && ip[3] == before[offset + 3] : HeaderSum(ip[..32]) == 65535;
            }
            check(correct, $"IPv{(ipv6 ? 6 : 4)} {priority} DSCP preserves ECN, VLAN, flow label/options and transport checksum");
        }
        using (var incoming = Packet(Flow(false), PacketDirection.Inbound, new byte[160]))
        {
            var before = incoming.Buffer.AsSpan(0, incoming.Length).ToArray();
            check(!QosPacketMarker.TryMark(incoming, PacketPriority.Voice) && incoming.Buffer.AsSpan(0, incoming.Length).SequenceEqual(before),
                "inbound DSCP is never rewritten");
        }

        var started = DateTimeOffset.UtcNow.AddDays(-1);
        var now = 0d;
        var currentFlow = Flow(false);
        var processName = "unknown-browser";
        OwnershipSnapshot Snapshot() => OwnershipSnapshot.Create([new(currentFlow, 301, started, processName)]);
        foreach (var ipv6 in new[] { false, true })
        foreach (var outbound in new[] { false, true })
        {
            currentFlow = Flow(ipv6) with { Protocol = TransportProtocol.Tcp };
            var policy = new RealtimeQosPolicy(Snapshot, () => now);
            policy.Protect([new(301, started.UtcTicks)]);
            using var packet = Packet(currentFlow, outbound ? PacketDirection.Outbound : PacketDirection.Inbound, new byte[200]);
            check(policy.Classify(packet) == PacketPriority.Game, $"game TCP IPv{(ipv6 ? 6 : 4)} {(outbound ? "outbound" : "inbound")} resolves PID/start");
            policy.Protect([new(301, started.AddSeconds(-1).UtcTicks)]);
            check(policy.Classify(packet) == PacketPriority.None, "reused PID does not inherit QoS game marking");
            policy.Protect([new(301, null)]);
            check(policy.Classify(packet) == PacketPriority.None, "missing game creation time is insufficient for DSCP elevation");
        }
        currentFlow = Flow(false);
        foreach (var name in new[] { "Discord", "Signal", "Telegram", "WhatsApp", "Skype", "Slack", "Guilded", "TeamSpeak" })
        {
            processName = name;
            var policy = new RealtimeQosPolicy(Snapshot, () => now);
            using var packet = Packet(currentFlow, PacketDirection.Outbound, new byte[200]);
            check(policy.Classify(packet) == PacketPriority.Voice, name + " small UDP is eligible without a running game");
            using var large = Packet(currentFlow, PacketDirection.Outbound, new byte[1200]);
            check(policy.Classify(large) == PacketPriority.None, name + " large video/file packets are not promoted to EF");
        }
        processName = "WhatsApp";
        {
            var policy = new RealtimeQosPolicy(Snapshot, () => now);
            var rejected = false;
            for (var i = 0; i < 30; i++) { using var packet = Packet(currentFlow, PacketDirection.Outbound, new byte[600]); rejected |= policy.Classify(packet) == PacketPriority.None; }
            now += .2;
            using var resumed = Packet(currentFlow, PacketDirection.Outbound, new byte[200]);
            check(rejected && policy.Classify(resumed) == PacketPriority.Voice, "EF burst/rate policing prevents unrestricted known-app promotion and recovers");
        }
        processName = "unknown-browser";
        foreach (var ipv6 in new[] { false, true })
        foreach (var direction in new[] { PacketDirection.Inbound, PacketDirection.Outbound })
        foreach (var pt in new byte[] { 0, 8, 18, 96, 111 })
        {
            currentFlow = Flow(ipv6);
            var policy = new RealtimeQosPolicy(Snapshot, () => now);
            var results = new List<PacketPriority>();
            for (var i = 0; i < 4; i++)
            {
                now += .02;
                using var packet = Packet(currentFlow, direction, Rtp(pt, unchecked((ushort)(65534 + i)), unchecked(0xffffff00u + (uint)i * 160)));
                results.Add(policy.Classify(packet));
            }
            check(pt is 0 or 8 or 18 ? results.Take(3).All(p => p == PacketPriority.None) && results[3] == PacketPriority.Voice : results.All(p => p == PacketPriority.None),
                $"IPv{(ipv6 ? 6 : 4)} {direction} RTP PT{pt}: static audio probation, dynamic/video remains unknown");
        }
        currentFlow = Flow(false);
        foreach (var invalid in new[] { "repeated-sequence", "repeated-timestamp", "changing-ssrc", "burst", "malformed-extension" })
        {
            var policy = new RealtimeQosPolicy(Snapshot, () => now);
            var safe = true;
            for (var i = 0; i < 8; i++)
            {
                now += invalid == "burst" ? 0 : .02;
                var data = Rtp(0, (ushort)(invalid == "repeated-sequence" ? 1 : i), invalid == "repeated-timestamp" ? 0 : (uint)i * 160);
                if (invalid == "changing-ssrc") data[11] = (byte)i;
                if (invalid == "malformed-extension") { data[0] |= 16; data[14] = 255; data[15] = 255; }
                using var packet = Packet(currentFlow, PacketDirection.Outbound, data);
                safe &= policy.Classify(packet) == PacketPriority.None;
            }
            check(safe, "generic voice rejects " + invalid);
        }
        {
            var stale = OwnershipSnapshot.Create([new(currentFlow, 301, started, "Discord")], DateTimeOffset.UtcNow.AddSeconds(-2));
            var policy = new RealtimeQosPolicy(() => stale);
            using var packet = Packet(currentFlow, PacketDirection.Outbound, new byte[200]);
            check(policy.Classify(packet) == PacketPriority.None, "stale ownership never establishes QoS elevation");
        }

        processName = "Discord";
        var sentDscp = 0;
        var source = new FakeDevice(packet => sentDscp = packet.Buffer[packet.IpOffset + 1] >> 2);
        var qosPolicy = new RealtimeQosPolicy(Snapshot);
        source.Enqueue(Packet(currentFlow, PacketDirection.Outbound, new byte[200]));
        await using (var pump = new PacketPump(source, null, null, qosPolicy.Classify))
        {
            await Until(() => pump.VoicePriorityPackets == 1);
            check(pump.Active && !pump.HasDownloadLimit && pump.QosEnabled && sentDscp == 46 && pump.SentBytes == pump.BypassedBytes && pump.BypassedBytes > 0,
                "QoS-only pump marks and sends voice immediately with no download pacing");
        }
        source = new FakeDevice(packet => sentDscp = packet.Buffer[packet.IpOffset + 1] >> 2);
        source.Enqueue(Packet(currentFlow, PacketDirection.Outbound, new byte[200]));
        await using (var pump = new PacketPump(source, .1, priority: _ => PacketPriority.Video))
        {
            await Until(() => pump.VideoPriorityPackets == 1);
            check(sentDscp == 34 && pump.BypassedBytes == pump.SentBytes && !pump.HasDownloadLimit,
                "video pump counts successful AF41 forwarding with obsolete caps inert");
        }
        source = new FakeDevice(_ => throw new IOException("failed QoS send"));
        source.Enqueue(Packet(currentFlow, PacketDirection.Outbound, new byte[200]));
        await using (var pump = new PacketPump(source, null, null, qosPolicy.Classify))
        {
            await Until(() => !pump.Active); await pump.DisposeAsync();
            check(pump.VoicePriorityPackets == 0 && pump.Failure?.Message == "failed QoS send", "priority counters do not claim failed native sends");
        }
        await Lifecycle(check);
        await DesktopStatus(check);
    }

    private static async Task Lifecycle(Action<bool, string> check)
    {
        var name = "dynex-qos-lifecycle-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var accept = server.WaitForConnectionAsync(timeout.Token); await client.ConnectAsync(timeout.Token); await accept;
        using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(server, leaveOpen: true);
        using var helperReader = new StreamReader(client, leaveOpen: true);
        using var helperWriter = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var stop = new CancellationTokenSource(); using var parent = Process.GetCurrentProcess();
        var created = 0; var devices = new List<FakeDevice>();
        Func<IPacketDevice> factory = () => { created++; var device = new FakeDevice(_ => { }); devices.Add(device); return device; };
        var run = typeof(PacketPump).Assembly.GetType("Dynex.Shaper.Program")!.GetMethod("RunConnectedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var helper = (Task<int>)run.Invoke(null, [parent, helperReader, helperWriter, stop, factory])!;
        await reader.ReadLineAsync(timeout.Token);
        JsonDocument state;
        do
        {
            await writer.WriteLineAsync("{\"mbps\":null,\"qosEnabled\":true}");
            state = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            if (state.RootElement.GetProperty("qosActive").GetBoolean()) break;
            state.Dispose(); await Task.Delay(50, timeout.Token);
        } while (true);
        using (state) check(!state.RootElement.GetProperty("active").GetBoolean() && state.RootElement.GetProperty("mbps").ValueKind == JsonValueKind.Null && created == 1,
            "QoS-only IPC is active independently of cap; repeated heartbeats retain capture");
        foreach (var limit in new double?[] { 8, 4, 12, null, 6, null })
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { mbps = limit, qosEnabled = true }));
            using (state = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!))
                check(created == 1 && !devices[0].Disposed && state.RootElement.GetProperty("qosActive").GetBoolean() &&
                    !state.RootElement.GetProperty("active").GetBoolean() && state.RootElement.GetProperty("mbps").ValueKind == JsonValueKind.Null,
                    "obsolete limit changes cannot reenable shaping or restart QoS capture: " + (limit?.ToString() ?? "no cap"));
        }
        await writer.WriteLineAsync("{\"mbps\":null}");
        using (state = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!))
            check(!state.RootElement.GetProperty("qosActive").GetBoolean() && devices[0].Disposed, "legacy null command releases a QoS-only session");
        await writer.WriteLineAsync("{\"mbps\":null,\"qosEnabled\":true}"); await reader.ReadLineAsync(timeout.Token);
        await writer.WriteLineAsync("{\"mbps\":null,\"shutdown\":true}");
        do { state = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!); if (state.RootElement.GetProperty("stopped").GetBoolean()) break; state.Dispose(); } while (true);
        using (state) check(!state.RootElement.GetProperty("qosActive").GetBoolean() && state.RootElement.GetProperty("releaseError").ValueKind == JsonValueKind.Null && devices.All(d => d.Disposed),
            "explicit shutdown ACK confirms QoS disabled and all fake devices released");
        check(await helper.WaitAsync(timeout.Token) == 0, "QoS-only helper shuts down cleanly");
    }

    private static async Task DesktopStatus(Action<bool, string> check)
    {
        var name = "dynex-qos-status-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = server.WaitForConnectionAsync(timeout.Token); await client.ConnectAsync(timeout.Token); await accept;
        using var output = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var controller = new ShaperController();
        var reader = (Task)typeof(ShaperController).GetMethod("ReadStatusAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, [server, timeout.Token])!;
        await output.WriteLineAsync("{\"active\":false,\"mbps\":null,\"qosActive\":true,\"gamePriorityPackets\":12,\"voicePriorityPackets\":34,\"videoPriorityPackets\":56}");
        await Until(() => controller.Status.QosActive);
        check(!controller.Status.Active && controller.Status.Mbps is null && controller.Status.GamePriorityPackets == 12 && controller.Status.VoicePriorityPackets == 34 && controller.Status.VideoPriorityPackets == 56,
            "desktop parses actual QoS-only status and priority counters");
        typeof(ShaperController).GetField("lastStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, Stopwatch.GetTimestamp() - 7 * Stopwatch.Frequency);
        check(!controller.Status.Active && !controller.Status.QosActive, "stale desktop QoS status loses activation claim");
        timeout.Cancel(); await reader;
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }
    internal static SocketFlow Flow(bool ipv6) => new(TransportProtocol.Udp,
        IPAddress.Parse(ipv6 ? "2001:db8:1::5" : "192.168.1.5"), 50001,
        IPAddress.Parse(ipv6 ? "2001:db8:2::8" : "192.168.1.1"), 49197);
    private static byte[] Rtp(byte pt, ushort sequence, uint timestamp)
    {
        var data = new byte[172]; data[0] = 128; data[1] = pt;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 12345);
        return data;
    }
    private static uint HeaderSum(ReadOnlySpan<byte> header)
    {
        uint sum = 0; for (var i = 0; i < header.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(header.Slice(i));
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16); return sum;
    }
    internal static CapturedPacket Packet(SocketFlow flow, PacketDirection direction, byte[] payload, int tags = 0, int options = 0)
    {
        var ipv6 = flow.LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var offset = 14 + tags * 4; var ipLength = (ipv6 ? 40 : 20 + options) + (flow.Protocol == TransportProtocol.Tcp ? 20 : 8) + payload.Length;
        var bytes = ArrayPool<byte>.Shared.Rent(offset + ipLength); bytes.AsSpan(0, offset + ipLength).Clear();
        for (var i = 0; i < tags; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12 + i * 4), 0x8100);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset - 2), ipv6 ? (ushort)0x86dd : (ushort)0x0800);
        var ip = bytes.AsSpan(offset, ipLength);
        var source = direction == PacketDirection.Outbound ? flow.LocalAddress : flow.RemoteAddress;
        var destination = direction == PacketDirection.Outbound ? flow.RemoteAddress : flow.LocalAddress;
        int transport;
        if (ipv6)
        {
            ip[0] = 0x60; ip[2] = 0xca; ip[3] = 0xfe; ip[6] = (byte)flow.Protocol;
            BinaryPrimitives.WriteUInt16BigEndian(ip[4..], (ushort)(ipLength - 40)); source.GetAddressBytes().CopyTo(ip[8..]); destination.GetAddressBytes().CopyTo(ip[24..]); transport = 40;
        }
        else
        {
            ip[0] = (byte)(0x45 + options / 4); ip[9] = (byte)flow.Protocol;
            BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ipLength); source.GetAddressBytes().CopyTo(ip[12..]); destination.GetAddressBytes().CopyTo(ip[16..]); transport = 20 + options;
            ip.Slice(20, options).Fill(1);
        }
        BinaryPrimitives.WriteUInt16BigEndian(ip[transport..], direction == PacketDirection.Outbound ? flow.LocalPort : flow.RemotePort);
        BinaryPrimitives.WriteUInt16BigEndian(ip[(transport + 2)..], direction == PacketDirection.Outbound ? flow.RemotePort : flow.LocalPort);
        var transportHeader = flow.Protocol == TransportProtocol.Tcp ? 20 : 8;
        if (transportHeader == 20) { ip[transport + 12] = 0x50; ip[transport + 16] = 0xbe; ip[transport + 17] = 0xef; }
        else { BinaryPrimitives.WriteUInt16BigEndian(ip[(transport + 4)..], (ushort)(8 + payload.Length)); ip[transport + 6] = 0xbe; ip[transport + 7] = 0xef; }
        payload.CopyTo(ip[(transport + transportHeader)..]);
        return new(bytes, offset + ipLength) { IpOffset = offset, Direction = direction };
    }
}
