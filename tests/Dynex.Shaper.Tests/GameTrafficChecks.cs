using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dynex.Desktop;
using Dynex.Desktop.Games;
using Dynex.Shaper;

internal static class GameTrafficChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var game = new ControlProtocol.ProtectedProcess(301, started.UtcTicks);
        foreach (var protocol in new[] { TransportProtocol.Tcp, TransportProtocol.Udp })
        foreach (var ipv6 in new[] { false, true })
        foreach (var direction in new[] { PacketDirection.Inbound, PacketDirection.Outbound })
        {
            var flow = new SocketFlow(protocol, IPAddress.Parse(ipv6 ? "fd00::1" : "192.168.1.2"), 51000,
                IPAddress.Parse(ipv6 ? "fd00::2" : "192.168.1.3"), 27015);
            var snapshot = OwnershipSnapshot.Create([new(flow, 301, started)]);
            double now = 100;
            var monitor = new GameTrafficMonitor(() => snapshot, () => now);
            monitor.Configure([game], true);
            check(monitor.Snapshot().Count == 0, "socket/bind ownership alone never creates game traffic activity");
            using var packet = Packet(flow, direction);
            var original = packet.Buffer.AsSpan(0, packet.Length).ToArray();
            monitor.Observe(packet);
            check(monitor.Snapshot() is [{ ProcessId: 301, AgeSeconds: 0 }],
                $"zero-payload {protocol} IPv{(ipv6 ? 6 : 4)} {direction} packet establishes LAN game traffic");
            check(original.SequenceEqual(packet.Buffer.AsSpan(0, packet.Length).ToArray()), "observation never modifies packet bytes");
            now += 9.999;
            check(monitor.Snapshot().Count == 1, "game traffic retains a ten-second monotone activity tail");
            now += .0011;
            check(monitor.Snapshot().Count == 0, "game traffic identity expires after ten seconds without packets");
            monitor.Observe(packet);
            check(monitor.Snapshot().Count == 1, "new packet reactivates an expired game without replacing capture");
            monitor.Configure([game with { StartedAtUtcTicks = started.AddSeconds(1).UtcTicks }], true);
            check(monitor.Snapshot().Count == 0, "PID reuse removes previous process activity immediately");
            monitor.Observe(packet);
            check(monitor.Snapshot().Count == 0, "old PID creation identity cannot activate its replacement");
        }

        var tcp = new SocketFlow(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.2"), 51000, IPAddress.Parse("198.51.100.1"), 443);
        var udp = tcp with { Protocol = TransportProtocol.Udp };
        foreach (var flow in new[] { tcp, udp })
        {
            foreach (var rows in new OwnershipEntry[][]
            {
                [], [new(flow, 999, started)], [new(flow, 301, null)],
                [new(flow, 301, started.AddSeconds(1))], [new(flow, 301, started), new(flow, 999, started)]
            })
            {
                var monitor = new GameTrafficMonitor(() => OwnershipSnapshot.Create(rows));
                monitor.Configure([game], true);
                using var packet = Packet(flow, PacketDirection.Outbound);
                monitor.Observe(packet);
                check(monitor.Snapshot().Count == 0, flow.Protocol + " unrelated, missing, reused or ambiguous owner never creates activity");
            }
            var stale = OwnershipSnapshot.Create([new(flow, 301, started)], DateTimeOffset.UtcNow.AddSeconds(-2));
            var staleMonitor = new GameTrafficMonitor(() => stale);
            staleMonitor.Configure([game], true);
            using var stalePacket = Packet(flow, PacketDirection.Inbound);
            staleMonitor.Observe(stalePacket);
            check(staleMonitor.Snapshot().Count == 0, flow.Protocol + " stale ownership never creates activity");
        }
        foreach (var address in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1" })
        {
            var flow = tcp with { LocalAddress = IPAddress.Parse(address), RemoteAddress = IPAddress.Parse(address) };
            var monitor = new GameTrafficMonitor(() => OwnershipSnapshot.Create([new(flow, 301, started)]));
            monitor.Configure([game], true);
            using var packet = Packet(flow, PacketDirection.Outbound);
            monitor.Observe(packet);
            check(monitor.Snapshot().Count == 0, "loopback traffic does not request network protection: " + address);
        }
        {
            var monitor = new GameTrafficMonitor(() => OwnershipSnapshot.Create([new(udp, 301, started)]));
            using var packet = Packet(udp, PacketDirection.Outbound);
            monitor.Configure([game with { StartedAtUtcTicks = null }], true);
            monitor.Observe(packet);
            check(monitor.Snapshot().Count == 0, "configured game without creation identity cannot activate traffic");
            monitor.Configure([game], true); monitor.Observe(packet);
            monitor.Configure([game], false);
            check(monitor.Snapshot().Count == 0, "disabled observation clears activity immediately");
            monitor.Observe(packet);
            check(monitor.Snapshot().Count == 0, "disabled monitor never recreates activity");
        }
        {
            var games = Enumerable.Range(1, 129).Select(id => new ControlProtocol.ProtectedProcess(id, started.UtcTicks)).ToArray();
            var owner = 128;
            var monitor = new GameTrafficMonitor(() => OwnershipSnapshot.Create([new(udp, owner, started)]));
            monitor.Configure(games, true);
            using var packet = Packet(udp, PacketDirection.Outbound);
            monitor.Observe(packet); owner = 129; monitor.Observe(packet);
            check(monitor.Snapshot() is [{ ProcessId: 128 }], "game observation identities are bounded to 128");
        }
        foreach (var throws in new[] { false, true })
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = 0;
            using var sample = Packet(udp, PacketDirection.Outbound);
            var original = sample.Buffer.AsSpan(0, sample.Length).ToArray();
            var device = new FakeDevice(packet =>
            {
                check(original.SequenceEqual(packet.Buffer.AsSpan(0, packet.Length).ToArray()), "observe-only delivery preserves every packet byte");
                sent.TrySetResult();
            });
            await using var pump = new PacketPump(device, null, _ => throw new Exception("unexpected pacing"),
                shouldLimitUpload: _ => throw new Exception("unexpected upload pacing"), observer: _ =>
                { Interlocked.Increment(ref observed); if (throws) throw new IOException("injected observation failure"); });
            device.Enqueue(Packet(udp, PacketDirection.Outbound));
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(observed == 1 && !pump.QosEnabled && !pump.HasDownloadLimit && !pump.HasUploadLimit && pump.DroppedPackets == 0,
                "independent observer has no QoS, pacing or loss even if observation throws: " + throws);
        }
        check(ControlProtocol.ParseCommand("{\"mbps\":null,\"observeGameTraffic\":true}") is
            { ObserveGameTraffic: true, Mbps: null, UploadMbps: null, QosEnabled: false }, "observe-only IPC never enables limits or QoS");
        await HelperLifecycle(check);
        await ControllerStatus(check, started);
        foreach (var frame in new[] { new string('x', 32769), "{\"active\":false,\"stopped\":true,\"releaseError\":null}" })
        {
            var controller = new ShaperController();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frame));
            var read = (Task)typeof(ShaperController).GetMethod("ReadStatusAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(controller, [stream, CancellationToken.None])!;
            await read;
            check(controller.Status.Message.StartsWith("Helferstatus ungültig") && controller.Status.ActiveGameProcesses is null,
                "oversized or partial status frames never establish successful observation or shutdown");
        }
    }

    private static async Task HelperLifecycle(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var name = "dynex-observe-helper-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync(timeout.Token); await client.ConnectAsync(timeout.Token); await accept;
        using var commands = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var replies = new StreamReader(server, leaveOpen: true);
        using var input = new StreamReader(client, leaveOpen: true);
        using var output = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var stop = new CancellationTokenSource(); using var parent = Process.GetCurrentProcess();
        var created = 0;
        FakeDevice? device = null;
        Func<IPacketDevice> factory = () => { created++; return device = new FakeDevice(_ => { }); };
        var run = typeof(PacketPump).Assembly.GetType("Dynex.Shaper.Program")!.GetMethod("RunConnectedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var helper = (Task<int>)run.Invoke(null, [parent, input, output, stop, factory])!;
        await replies.ReadLineAsync(timeout.Token);
        for (var i = 0; i < 2; i++)
        {
            await commands.WriteLineAsync("{\"mbps\":null,\"observeGameTraffic\":true,\"commandId\":81}");
            using var status = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!);
            var root = status.RootElement;
            check(created == 1 && device is { Disposed: false } && !root.GetProperty("captureReleased").GetBoolean() &&
                !root.GetProperty("active").GetBoolean() && !root.GetProperty("qosActive").GetBoolean() && !root.GetProperty("uploadActive").GetBoolean() &&
                root.GetProperty("activeGameProcesses").GetArrayLength() == 0,
                "observe-only helper retains idle capture and never enables rates or QoS");
        }
        foreach (var rate in new double?[] { 8, null })
        {
            await commands.WriteLineAsync(JsonSerializer.Serialize(new { mbps = rate, observeGameTraffic = true }));
            using var status = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!);
            check(created == 1 && device is { Disposed: false } && !status.RootElement.GetProperty("captureReleased").GetBoolean() &&
                !status.RootElement.GetProperty("qosActive").GetBoolean(), "observe and rate transitions retain the same bidirectional device");
        }
        foreach (var transition in new (bool Qos, bool Observe, int Devices)[] { (true, true, 2), (true, false, 2), (true, true, 2), (false, true, 3) })
        {
            var oldDevice = device;
            await commands.WriteLineAsync(JsonSerializer.Serialize(new { mbps = (double?)null,
                qosEnabled = transition.Qos, observeGameTraffic = transition.Observe }));
            using var status = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!);
            check(created == transition.Devices && device is { Disposed: false } &&
                (ReferenceEquals(oldDevice, device) || oldDevice is { Disposed: true }) &&
                !status.RootElement.GetProperty("captureReleased").GetBoolean(),
                "QoS/observe transitions retain capture or confirm old-device disposal before replacement");
        }
        await commands.WriteLineAsync("{\"mbps\":null,\"observeGameTraffic\":false,\"commandId\":82}");
        using (var idle = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!))
            check(device is { Disposed: true } && idle.RootElement.GetProperty("captureReleased").GetBoolean() &&
                !idle.RootElement.GetProperty("observingGameTraffic").GetBoolean(), "stopping observation releases the packet device with matching acknowledgement");
        await commands.WriteLineAsync("{\"mbps\":null,\"shutdown\":true}");
        while (await replies.ReadLineAsync(timeout.Token) is { } line)
        {
            using var final = JsonDocument.Parse(line);
            if (final.RootElement.GetProperty("stopped").GetBoolean()) break;
        }
        check(await helper.WaitAsync(timeout.Token) == 0, "observe session shutdown confirms cleanup without a kernel filter");
    }

    private static async Task ControllerStatus(Action<bool, string> check, DateTimeOffset started)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var name = "dynex-observe-desktop-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync(timeout.Token); await client.ConnectAsync(timeout.Token); await accept;
        var controller = new ShaperController();
        void Set(string field, object value) => typeof(ShaperController).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, value);
        Set("pipe", server); Set("startAttempted", true);
        Set("writer", new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true });
        using var lifetime = new CancellationTokenSource(); Set("cancellation", lifetime);
        var reading = (Task)typeof(ShaperController).GetMethod("ReadStatusAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, [server, lifetime.Token])!;
        Set("reader", reading);
        using var input = new StreamReader(client, leaveOpen: true);
        var output = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var games = new[] { new GameDetectionResult("Synthetic", "Synthetic", 301, started, true, "Configured") };
        var applying = controller.ApplyAsync(8, timeout.Token, games, true, () => false, 2, true, () => true);
        using (var command = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!))
            check(command.RootElement.GetProperty("mbps").ValueKind == JsonValueKind.Null &&
                !command.RootElement.GetProperty("qosEnabled").GetBoolean() && command.RootElement.GetProperty("observeGameTraffic").GetBoolean(),
                "post-start protection recheck can remove limits while preserving independently needed observation");
        await applying;
        await output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, observingGameTraffic = true,
            activeGameProcesses = Enumerable.Range(1, 128).Select(id => new { processId = id, startedAtUtcTicks = started.UtcTicks, ageSeconds = 0d }) }));
        await Wait(() => controller.Status.ActiveGameProcesses?.Count == 128);
        check(controller.Status is { ObservingGameTraffic: true, Active: false, QosActive: false } && !controller.CaptureReleased,
            "desktop accepts 128 bounded observation records while capture remains owned");
        Set("lastStatus", Stopwatch.GetTimestamp() - 7 * Stopwatch.Frequency);
        check(controller.Status is { ObservingGameTraffic: false, ActiveGameProcesses: null }, "six-second stale observation status becomes unknown, not certified no-traffic");
        await output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, observingGameTraffic = true,
            activeGameProcesses = new[] { new { processId = 301, startedAtUtcTicks = started.UtcTicks, ageSeconds = 9d } } }));
        await Wait(() => controller.Status.ActiveGameProcesses?.Count == 1);
        Set("activeGameExpirations", new Dictionary<GameTrafficIdentity, long> { [new(301, started.UtcTicks)] = Stopwatch.GetTimestamp() - 1 });
        check(controller.Status.ActiveGameProcesses is { Count: 0 } && controller.Status.ObservingGameTraffic,
            "desktop independently expires packet evidence between helper reports");
        await output.WriteLineAsync("{\"active\":false,\"mbps\":null}");
        await Wait(() => controller.Status.Message.Contains("nicht unterstützt"));
        check(controller.Status is { ObservingGameTraffic: false, ActiveGameProcesses: null }, "old helper cannot fake successful no-traffic observation");
        applying = controller.ApplyAsync(null, timeout.Token, games, observeGameTraffic: true, observationStillNeeded: () => false);
        using var idle = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!);
        await applying;
        check(!idle.RootElement.GetProperty("observeGameTraffic").GetBoolean(), "post-start observation recheck suppresses obsolete monitoring");
        var id = idle.RootElement.GetProperty("commandId").GetInt64();
        await output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, observingGameTraffic = true, activeGameProcesses = Array.Empty<object>(),
            commandId = id, captureReleased = true, releaseError = (string?)null, stopped = true }));
        await Wait(() => controller.Status.ObservingGameTraffic);
        check(!controller.CaptureReleased, "observation-active status cannot acknowledge release or shutdown even if unrelated inactive flags claim it");
        var stopping = controller.StopAsync(requireExit: true);
        await input.ReadLineAsync(timeout.Token);
        await output.WriteLineAsync("{\"active\":false,\"observingGameTraffic\":false,\"stopped\":true,\"releaseError\":null}");
        output.Dispose(); client.Dispose(); await stopping;
        check(controller.CaptureReleased, "desktop confirms observe-only shutdown after explicit final release");
    }

    private static async Task Wait(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Observation test condition timed out");
            await Task.Delay(5);
        }
    }

    private static CapturedPacket Packet(SocketFlow flow, PacketDirection direction)
    {
        var ipv6 = flow.LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var header = ipv6 ? 40 : 20;
        var transport = flow.Protocol == TransportProtocol.Tcp ? 20 : 8;
        var bytes = ArrayPool<byte>.Shared.Rent(header + transport); bytes.AsSpan(0, header + transport).Clear();
        var ip = bytes.AsSpan(0, header + transport);
        ip[0] = ipv6 ? (byte)0x60 : (byte)0x45;
        ip[ipv6 ? 6 : 9] = (byte)flow.Protocol;
        BinaryPrimitives.WriteUInt16BigEndian(ip[(ipv6 ? 4 : 2)..], (ushort)(ipv6 ? transport : header + transport));
        var source = direction == PacketDirection.Outbound ? flow.LocalAddress : flow.RemoteAddress;
        var target = direction == PacketDirection.Outbound ? flow.RemoteAddress : flow.LocalAddress;
        source.GetAddressBytes().CopyTo(ip[(ipv6 ? 8 : 12)..]); target.GetAddressBytes().CopyTo(ip[(ipv6 ? 24 : 16)..]);
        BinaryPrimitives.WriteUInt16BigEndian(ip[header..], direction == PacketDirection.Outbound ? flow.LocalPort : flow.RemotePort);
        BinaryPrimitives.WriteUInt16BigEndian(ip[(header + 2)..], direction == PacketDirection.Outbound ? flow.RemotePort : flow.LocalPort);
        if (transport == 20) ip[header + 12] = 0x50;
        else BinaryPrimitives.WriteUInt16BigEndian(ip[(header + 4)..], 8);
        return new(bytes, header + transport) { Direction = direction };
    }
}
