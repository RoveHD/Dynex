using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Dynex.Shaper;
using Dynex.Desktop;
using System.Text;
using System.Text.Json;

if (args is ["--process-lifetime-child"])
{
    await Console.In.ReadLineAsync();
    return;
}

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch { Check(true, name); return; }
    throw new Exception("FAIL: accepted " + name);
}

foreach (var value in new[] { double.NaN, double.PositiveInfinity, 0, -.1, .09, 100001 })
    Reject(() => new PacketPacer(value), $"reject invalid rate {value}");
var pacer = new PacketPacer(8); // 1 MB/s
var end = 0d;
for (var i = 0; i < 1000; i++) end = pacer.Schedule(1000, 10);
Check(Math.Abs(end - 10.99) < .000001, "byte-based aggregate budget at 8 Mbit/s");
Check(Math.Abs(pacer.Schedule(1000, 1000) - 1000) < .000001, "idle credit is bounded to 10 ms");
var fast = new PacketPacer(10000);
Check(fast.Schedule(65575, 1) > 1, "high-rate idle credit capped at 64 KiB");
Reject(() => pacer.Schedule(0, 1), "reject empty packet");
Reject(() => pacer.Schedule(65576, 1), "reject oversized packet");
Check(ControlProtocol.ParseRate("{\"mbps\":null}") is null, "unlimited command");
Check(ControlProtocol.ParseRate("{\"mbps\":12.5}") == 12.5, "decimal Mbit command");
Reject(() => ControlProtocol.ParseRate("{}"), "missing mbps rejected");
Reject(() => ControlProtocol.ParseRate("{\"mbps\":0}"), "zero cannot silently disable limit");
Reject(() => ControlProtocol.ParseRate("{\"mbps\":\"10\"}"), "string rate rejected");
using (var input = new StringReader(new string('x', ControlProtocol.MaxLineLength + 1) + "\n"))
{
    try { await ControlProtocol.ReadLineAsync(input, default); throw new Exception("unbounded line accepted"); }
    catch (FormatException) { Check(true, "IPC input memory bounded"); }
}
using (var input = new StringReader("{\"mbps\":null}"))
{
    try { await ControlProtocol.ReadLineAsync(input, default); throw new Exception("partial command accepted"); }
    catch (EndOfStreamException) { Check(true, "partial IPC command rejected"); }
}
Check(InternetFilter.Range("ip.SrcAddr", IPAddress.Parse("198.51.100.129"), 24) ==
      "(ip.SrcAddr >= 198.51.100.0 and ip.SrcAddr <= 198.51.100.255)", "IPv4 public on-link LAN prefix");
Check(InternetFilter.Range("ipv6.SrcAddr", IPAddress.Parse("2001:db8:1234:5678::1"), 64) ==
      "(ipv6.SrcAddr >= 2001:db8:1234:5678:: and ipv6.SrcAddr <= 2001:db8:1234:5678:ffff:ffff:ffff:ffff)", "IPv6 prefix masking");
var filter = InternetFilter.Build([(IPAddress.Parse("1.2.3.4"), 0)]);
Check(filter.Contains("inbound and !loopback and !impostor") && filter.Contains("tcp.PayloadLength > 0"), "inbound payload only; recursion and control bypass");
Check(filter.Contains("udp.SrcPort == 53") && filter.Contains("udp.DstPort == 5353") && filter.Contains("tcp.DstPort != 53"), "DNS/discovery bypass for both transports");
Check(filter.Contains("fc00::") && filter.Contains("fe80::") && filter.Contains("192.168.0.0"), "private LAN and IPv6 link-local bypass");
Check(!filter.Contains("255.255.255.255) or (ip.SrcAddr >= 1."), "default prefix does not bypass Internet");
var addressType = typeof(PacketPump).Assembly.GetType("Dynex.Shaper.Native+Address", throwOnError: true)!;
Check(Marshal.SizeOf(addressType) == 80, "native address ABI has 80 bytes");

// Named-pipe loopback exercises the actual line framing without elevation or driver.
var name = "dynex-test-" + Guid.NewGuid().ToString("N");
using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
           PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var accept = server.WaitForConnectionAsync(timeout.Token);
    await client.ConnectAsync(timeout.Token);
    await accept;
    using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(client, leaveOpen: true);
    var read = ControlProtocol.ReadLineAsync(reader, timeout.Token);
    await writer.WriteLineAsync("{\"mbps\":4.5}".AsMemory(), timeout.Token);
    var line = await read;
    Check(ControlProtocol.ParseRate(line!) == 4.5, "CurrentUserOnly named-pipe command roundtrip");
}

// Real TCP loopback sink, synthetic source. TCP avoids unrelated firewall/socket
// UDP losses in the test host. Tests pacing and sending, never packet diversion.
using (var listener = new TcpListener(IPAddress.Loopback, 0))
using (var socket = new TcpClient(AddressFamily.InterNetwork))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    listener.Start();
    var accept = listener.AcceptTcpClientAsync(timeout.Token);
    await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
    using var sink = await accept;
    socket.NoDelay = true;
    using var output = socket.GetStream();
    using var input = sink.GetStream();
    var source = new FakeDevice(packet => output.Write(packet.Buffer, 0, packet.Length));
    for (var i = 0; i < 32; i++) source.Enqueue(1500);
    var watch = Stopwatch.StartNew();
    await using var pump = new PacketPump(source, .1);
    await input.ReadExactlyAsync(new byte[48000], timeout.Token);
    watch.Stop();
    Check(watch.Elapsed.TotalSeconds < 2,
        $"legacy 0.1 Mbit/s cap is inert through TCP loopback ({watch.Elapsed.TotalMilliseconds:F0} ms / 48000 bytes)");
    await WaitUntil(() => pump.SentBytes == 48000, TimeSpan.FromSeconds(1));
    Check(pump.SentBytes == 48000 && pump.DroppedPackets == 0, "pacing preserves packets within bounded backlog");
    await Task.WhenAll(pump.DisposeAsync().AsTask(), pump.DisposeAsync().AsTask());
    Check(source.Disposed && !pump.Active, "concurrent stop is idempotent and interrupts blocked receive");
}

{
    var source = new FakeDevice(_ => throw new IOException("injected send failure"));
    source.Enqueue(1000);
    await using var pump = new PacketPump(source, 10);
    await WaitUntil(() => !pump.Active, TimeSpan.FromSeconds(2));
    await pump.DisposeAsync(); // Active clears before synchronous device cleanup completes.
    Check(source.Disposed && pump.Failure?.Message == "injected send failure", "send failure immediately closes device (fail open)");
}
{
    var source = new FakeDevice(_ => { });
    source.ReceiveFailure = new IOException("injected receive failure");
    await using var pump = new PacketPump(source, 10);
    await WaitUntil(() => !pump.Active, TimeSpan.FromSeconds(2));
    await pump.DisposeAsync();
    Check(source.Disposed && pump.Failure?.Message == "injected receive failure", "receive failure immediately closes device (fail open)");
}
foreach (var throws in new[] { false, true })
{
    var source = new FakeDevice(_ => { })
    {
        CleanupError = throws ? null : "injected adapter reset failure",
        DisposeFailure = throws ? new IOException("injected disposal failure") : null
    };
    await using var pump = new PacketPump(source, 8);
    await pump.DisposeAsync();
    Check(!pump.Active && pump.CleanupError == (throws ? "injected disposal failure" : "injected adapter reset failure") &&
        pump.Failure is PacketReleaseException, "native-style cleanup failure survives pump stop: " + throws);
}

// Exercise the real helper IPC loop with a fake device: no elevated start or
// native capture. The desktop consumes the same final release acknowledgement.
foreach (var cleanupError in new string?[] { null, "injected native flush failure" })
{
    var pipeName = "dynex-release-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var accept = server.WaitForConnectionAsync(timeout.Token);
    await client.ConnectAsync(timeout.Token); await accept;
    using var commands = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    using var replies = new StreamReader(server, leaveOpen: true);
    using var helperReader = new StreamReader(client, leaveOpen: true);
    using var helperWriter = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    using var helperStop = new CancellationTokenSource();
    using var parent = Process.GetCurrentProcess();
    var created = 0;
    Func<IPacketDevice> factory = () => { created++; return new FakeDevice(_ => { }) { CleanupError = cleanupError }; };
    var run = typeof(PacketPump).Assembly.GetType("Dynex.Shaper.Program")!
        .GetMethod("RunConnectedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
    var helper = (Task<int>)run.Invoke(null, [parent, helperReader, helperWriter, helperStop, factory])!;
    await replies.ReadLineAsync(timeout.Token); // startup
    await commands.WriteLineAsync("{\"mbps\":null,\"qosEnabled\":false,\"commandId\":72}");
    using (var idle = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!))
        Check(created == 0 && idle.RootElement.GetProperty("captureReleased").GetBoolean() &&
            idle.RootElement.GetProperty("commandId").GetInt64() == 72,
            "idle helper heartbeat acknowledges no capture without constructing any device");
    await commands.WriteLineAsync("{\"mbps\":8,\"qosEnabled\":true}");
    await replies.ReadLineAsync(timeout.Token);
    await commands.WriteLineAsync("{\"mbps\":null,\"qosEnabled\":false,\"commandId\":73}");
    using (var released = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!))
    {
        Check(cleanupError is null ? released.RootElement.GetProperty("releaseError").ValueKind == JsonValueKind.Null :
            released.RootElement.GetProperty("releaseError").GetString()!.Contains(cleanupError),
            "helper preserves exact release result before discarding pump");
        Check(released.RootElement.GetProperty("commandId").GetInt64() == 73 &&
            released.RootElement.GetProperty("captureReleased").GetBoolean() == (cleanupError is null),
            "idle release acknowledgement identifies this command and requires successful device disposal");
    }
    if (cleanupError is not null)
    {
        await commands.WriteLineAsync("{\"mbps\":8}");
        using var blocked = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!);
        Check(!blocked.RootElement.GetProperty("active").GetBoolean() && created == 1 &&
            blocked.RootElement.GetProperty("releaseError").GetString()!.Contains(cleanupError),
            "unconfirmed release blocks rearm and error remains sticky");
    }
    await commands.WriteLineAsync("{\"mbps\":null,\"shutdown\":true}");
    JsonDocument? final = null;
    do
    {
        final?.Dispose();
        final = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!);
    } while (!final.RootElement.GetProperty("stopped").GetBoolean());
    using (final)
        Check(cleanupError is null ? final.RootElement.GetProperty("releaseError").ValueKind == JsonValueKind.Null :
            final.RootElement.GetProperty("releaseError").GetString()!.Contains(cleanupError),
            "final shutdown acknowledgement carries cleanup result");
    Check(await helper.WaitAsync(timeout.Token) == (cleanupError is null ? 0 : 1), "helper exits with actual release result");
}

{
    using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"mbps\":8,\"qosEnabled\":true}\n"));
    using var output = new MemoryStream();
    using var commands = new StreamReader(input);
    using var replies = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    using var stop = new CancellationTokenSource();
    using var parent = Process.GetCurrentProcess();
    Func<IPacketDevice> factory = () => new FakeDevice(_ => { }) { CleanupError = "injected EOF reset failure" };
    var run = typeof(PacketPump).Assembly.GetType("Dynex.Shaper.Program")!
        .GetMethod("RunConnectedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
    var helper = (Task<int>)run.Invoke(null, [parent, commands, replies, stop, factory])!;
    Check(await helper.WaitAsync(TimeSpan.FromSeconds(5)) == 1, "EOF cleanup failure determines exit code after finally");
    using var final = JsonDocument.Parse(Encoding.UTF8.GetString(output.ToArray()).Trim().Split('\n')[^1]);
    Check(final.RootElement.GetProperty("stopped").GetBoolean() &&
        final.RootElement.GetProperty("releaseError").GetString()!.Contains("injected EOF reset failure"),
        "EOF final acknowledgement retains late cleanup error");
}

// Link the actual desktop controller into this runner. Inject only its connected
// pipe, never call StartAsync or PacketFilterSetup; all stop/read logic is real.
foreach (var outcome in new[] { "success", "cleanup-error", "stale-eof", "closed", "timeout", "qos-apply-eof", "qos-still-active" })
{
    var pipeName = "dynex-desktop-release-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var accept = server.WaitForConnectionAsync(timeout.Token);
    await client.ConnectAsync(timeout.Token); await accept;
    var controller = new ShaperController();
    void SetField(string field, object value) => typeof(ShaperController).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, value);
    var desktopLifetime = new CancellationTokenSource();
    SetField("pipe", server);
    SetField("writer", new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true });
    SetField("cancellation", desktopLifetime);
    SetField("captureRequested", outcome != "qos-apply-eof");
    var readerTask = (Task)typeof(ShaperController).GetMethod("ReadStatusAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(controller, [server, desktopLifetime.Token])!;
    SetField("reader", readerTask);
    var peer = Task.Run(async () =>
    {
        if (outcome == "closed") { client.Dispose(); return; }
        try
        {
            using var input = new StreamReader(client, leaveOpen: true);
            using var output = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var command = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!);
            if (outcome == "qos-apply-eof")
            {
                Check(command.RootElement.GetProperty("qosEnabled").GetBoolean() && command.RootElement.GetProperty("mbps").ValueKind == JsonValueKind.Null,
                    "desktop sends QoS-only Apply without a cap");
                return;
            }
            Check(command.RootElement.GetProperty("shutdown").GetBoolean(), "desktop sends explicit shutdown request: " + outcome);
            await output.WriteLineAsync("{\"active\":false,\"mbps\":null,\"error\":null,\"releaseError\":null}");
            if (outcome == "timeout") { await Task.Delay(Timeout.Infinite, timeout.Token); return; }
            if (outcome != "stale-eof")
                await output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, mbps = (double?)null, stopped = true,
                    qosActive = outcome == "qos-still-active",
                    error = outcome == "cleanup-error" ? "injected native reset failure" : null,
                    releaseError = outcome == "cleanup-error" ? "injected native reset failure" : null }));
        }
        finally { client.Dispose(); }
    });
    if (outcome == "qos-apply-eof")
    {
        await controller.ApplyAsync(null, timeout.Token, qosEnabled: true);
        await peer; await readerTask;
    }
    var rejected = false;
    try { await controller.StopAsync(requireExit: true).WaitAsync(timeout.Token); }
    catch (InvalidOperationException) { rejected = true; }
    Check(outcome == "success" ? !rejected && controller.Status.Message == "Paketpriorisierung ausgeschaltet" :
        rejected && controller.Status.Message.Contains(outcome == "cleanup-error" ? "injected native reset failure" : "nicht bestätigt"),
        "desktop requires final release confirmation and preserves errors: " + outcome);
    await controller.StopAsync();
    Check(outcome == "success" || controller.Status.Message != "Paketpriorisierung ausgeschaltet",
        "repeated stop cannot erase unconfirmed release: " + outcome);
    timeout.Cancel();
    try { await peer; } catch (OperationCanceledException) { }
}
{
    var controller = new ShaperController();
    await controller.StopAsync(requireExit: true);
    Check(controller.Status.Message == "Paketpriorisierung ausgeschaltet", "stop before writer/connection creation is safe");
}
{
    var controller = new ShaperController();
    Check(controller.CaptureReleased && !controller.StartAttempted, "unused controller confirms idle without a helper launch");
    await controller.ApplyAsync(null, default);
    Check(!controller.StartAttempted, "idle Apply never requests a privileged helper startup");
    // Test runner outputs contain no packaged helper. Missing-component failure
    // takes the exact guarded startup path without elevation or native capture.
    Check(!controller.IsAvailable, "startup failure fixture has no packaged helper");
    await controller.ApplyAsync(8, default, qosEnabled: true);
    Check(controller.StartAttempted && controller.Status.Message.Contains("fehlt"),
        "first gaming request attempts startup and reports its actual failure");
    typeof(ShaperController).GetMethod("Set", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller,
        [new ShaperController.ShaperStatus(false, null, "startup sentinel")]);
    await controller.ApplyAsync(8, default, qosEnabled: true);
    await controller.ApplyAsync(null, default);
    await controller.ApplyAsync(8, default, qosEnabled: true);
    Check(controller.Status.Message == "startup sentinel" && controller.StartAttempted,
        "repeated gaming and Alt-Tab heartbeats do not retry failed startup or UAC");
    await controller.StopAsync();
    Check(!controller.StartAttempted, "an explicit disable/app stop permits one future startup attempt");
}
foreach (var releaseError in new string?[] { null, "injected release failure" })
{
    var pipeName = "dynex-idle-ack-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var accept = server.WaitForConnectionAsync(timeout.Token);
    await client.ConnectAsync(timeout.Token); await accept;
    var controller = new ShaperController();
    void Set(string field, object value) => typeof(ShaperController).GetField(field,
        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, value);
    Set("pipe", server);
    Set("writer", new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true });
    Set("cancellation", timeout);
    Set("captureRequested", true);
    Set("reader", typeof(ShaperController).GetMethod("ReadStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(controller, [server, timeout.Token])!);
    using var input = new StreamReader(client, leaveOpen: true);
    var output = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    var releasing = controller.ApplyAsync(null, timeout.Token);
    using var idle = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!);
    var id = idle.RootElement.GetProperty("commandId").GetInt64();
    await output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, qosActive = false,
        mbps = (double?)null, commandId = id, captureReleased = false, releaseError = (string?)null }));
    await releasing;
    async Task Reply(long commandId, bool captureReleased, string message, string? error = null)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, qosActive = false,
            mbps = (double?)null, commandId, captureReleased, releaseError = error, error = message }));
        await WaitUntil(() => controller.Status.Message == (error ?? message), TimeSpan.FromSeconds(2));
    }
    await Reply(id - 1, true, "stale reply");
    Check(!controller.CaptureReleased, "stale idle status cannot acknowledge a newer capture release");
    await Reply(id, false, "capture still owned");
    Check(!controller.CaptureReleased, "inactive QoS/rate alone cannot confirm the packet device was released");
    await Reply(id, true, "current release", releaseError);
    Check(controller.CaptureReleased == (releaseError is null),
        "matching release acknowledgement is accepted only without a native cleanup error");
    if (releaseError is null)
    {
        // A foreground change while StartAsync is pending must prevent the
        // actual command from reactivating capture after UAC finishes.
        var recheckedAfterStart = false;
        var applying = controller.ApplyAsync(8, timeout.Token, qosEnabled: true, protectionStillNeeded: () =>
        { recheckedAfterStart = controller.StartAttempted; return false; });
        using var skipped = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!);
        await output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, qosActive = false,
            mbps = (double?)null, commandId = skipped.RootElement.GetProperty("commandId").GetInt64(),
            captureReleased = true, releaseError = (string?)null }));
        await applying;
        Check(recheckedAfterStart && skipped.RootElement.GetProperty("mbps").ValueKind == JsonValueKind.Null &&
            !skipped.RootElement.GetProperty("qosEnabled").GetBoolean() && controller.CaptureReleased,
            "live foreground/peer recheck after helper startup suppresses obsolete capture requests");
    }
    var stopping = controller.StopAsync();
    using var shutdown = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!);
    await output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, qosActive = false, mbps = (double?)null,
        stopped = true, releaseError }));
    output.Dispose();
    client.Dispose();
    await stopping;
    Check(controller.CaptureReleased == (releaseError is null), "shutdown cannot erase an unconfirmed idle release");
}
{
    var source = new FakeDevice(_ => { });
    for (var i = 0; i < 128; i++) source.Enqueue(1500);
    var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
    await using var pump = new PacketPump(source, 1000);
    await WaitUntil(() => pump.SentBytes == 192000, TimeSpan.FromSeconds(2));
    var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
    Check(pump.DroppedPackets == 0 && cpu < TimeSpan.FromSeconds(.5),
        $"high-rate path yields without CPU spin ({cpu.TotalMilliseconds:F0} ms process CPU)");
}
{
    var source = new FakeDevice(_ => { });
    for (var i = 0; i < 2000; i++) source.Enqueue(1500);
    await using var pump = new PacketPump(source, .1);
    await WaitUntil(() => pump.SentBytes == 3_000_000, TimeSpan.FromSeconds(2));
    Check(pump.DroppedPackets == 0 && !pump.HasDownloadLimit, "bulk traffic passes completely without shaping drops despite a legacy low cap");
    var watch = Stopwatch.StartNew();
    await pump.DisposeAsync();
    Check(watch.Elapsed < TimeSpan.FromSeconds(1), "stop interrupts pacing and backlog immediately");
}
{
    var prioritySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var source = new FakeDevice(packet => { if (packet.Length == 64) prioritySent.TrySetResult(); });
    for (var i = 0; i < 100; i++) source.Enqueue(1500);
    await using var pump = new PacketPump(source, .1, packet => packet.Length != 64);
    await WaitUntil(() => pump.ReceivedBytes >= 150000, TimeSpan.FromSeconds(2));
    source.Enqueue(64);
    await prioritySent.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Check(pump.SentBytes == 150064 && pump.BypassedBytes == 150064, "bulk and interactive packets are both forwarded immediately without a download queue");
}
NdisPacketDevice.ValidateAbi();
NdisLifecycleChecks.Run(Check);
{
    var delivered = new ConcurrentQueue<int>();
    var source = new FakeDevice(packet => delivered.Enqueue(packet.Length));
    await using var pump = new PacketPump(source, 8, packet => packet.Length > 128, _ => PacketPriority.None);
    var expected = 0;
    foreach (var rate in new double?[] { 4, 12, null, 8, null })
    {
        pump.UpdateLimit(rate);
        source.Enqueue(64);
        source.Enqueue(100);
        expected += 2;
        await WaitUntil(() => delivered.Count == expected, TimeSpan.FromSeconds(2));
    }
    await WaitUntil(() => delivered.Count == 10, TimeSpan.FromSeconds(2));
    Check(!source.Disposed && pump.Active && !pump.HasDownloadLimit && pump.DroppedPackets == 0 &&
        delivered.Count(x => x == 64) == 5 && delivered.Count(x => x == 100) == 5,
        "rate changes preserve the device and every unshaped control packet");
    source.Enqueue(1500);
    await WaitUntil(() => delivered.Count == 11 && pump.BypassedBytes == 2320, TimeSpan.FromSeconds(2));
    Check(pump.BypassedBytes == 2320, "removing a cap keeps QoS capture and immediately bypasses future bulk traffic");
}
Check(NdisPacketDevice.GetAbiSizes() == (12, 16, 52, 1566, 8836), "NDIS x64 ABI layout matches official packed structures");
var ethernet = new byte[80];
ethernet[12] = 8; ethernet[13] = 0; ethernet[14] = 0x45;
Check(NdisPacketDevice.GetIpOffset(ethernet) == 14, "Ethernet IPv4 offset");
ethernet[12] = 0x86; ethernet[13] = 0xdd; ethernet[14] = 0x60;
Check(NdisPacketDevice.GetIpOffset(ethernet) == 14, "Ethernet IPv6 offset");
ethernet[12] = 0x81; ethernet[13] = 0; ethernet[16] = 8; ethernet[17] = 0; ethernet[18] = 0x45;
Check(NdisPacketDevice.GetIpOffset(ethernet) == 18, "single VLAN offset");
ethernet[16] = 0x88; ethernet[17] = 0xa8; ethernet[20] = 8; ethernet[21] = 0; ethernet[22] = 0x45;
Check(NdisPacketDevice.GetIpOffset(ethernet) == 22, "double VLAN offset");
ethernet[20] = 0x81; ethernet[21] = 0;
Check(NdisPacketDevice.GetIpOffset(ethernet) == -1, "unsupported third VLAN tag bypasses");
Check(NdisPacketDevice.GetIpOffset(ethernet.AsSpan(0, 15)) == -1, "truncated VLAN bypasses");
ethernet[12] = 8; ethernet[13] = 6;
Check(NdisPacketDevice.GetIpOffset(ethernet) == -1, "ARP bypasses");
await QosChecks.Run(Check);
RealtimeVideoChecks.Run(Check);
StreamingChecks.Run(Check);
await MediaEvidenceChecks.Run(Check);
await GameTrafficChecks.Run(Check);
await HelperRecoveryChecks.Run(Check);
await PrioritizationServiceChecks.Run(Check);
Console.WriteLine($"{passed} checks passed; no kernel filter opened.");

static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
{
    var start = Stopwatch.GetTimestamp();
    while (!condition())
    {
        if (Stopwatch.GetElapsedTime(start) > timeout) throw new TimeoutException("Test condition timed out.");
        await Task.Delay(5);
    }
}

sealed class FakeDevice(Action<CapturedPacket> send) : IPacketDevice
{
    private readonly BlockingCollection<CapturedPacket> _source = new();
    public bool Disposed { get; private set; }
    public Exception? ReceiveFailure { get; set; }
    public string? CleanupError { get; set; }
    public Exception? DisposeFailure { get; set; }
    public void Enqueue(int length) => _source.Add(new CapturedPacket(ArrayPool<byte>.Shared.Rent(length), length));
    public void Enqueue(CapturedPacket packet) => _source.Add(packet);
    public CapturedPacket Receive() => ReceiveFailure is null ? _source.Take() : throw ReceiveFailure;
    public void Send(CapturedPacket packet) => send(packet);
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        _source.CompleteAdding();
        while (_source.TryTake(out var packet)) packet.Dispose();
        if (DisposeFailure is not null) throw DisposeFailure;
    }
}
