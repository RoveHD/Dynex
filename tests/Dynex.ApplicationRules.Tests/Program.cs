using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dynex.Desktop;
using Dynex.Shaper;

var count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is ArgumentException or FormatException or System.Text.Json.JsonException or InvalidOperationException) { count++; return; } throw new Exception(name); }
Check(ApplicationRules.Normalize([new(" Steam.EXE ", 30, 5)])[0].ProcessName == "steam", "names normalize");
foreach (var name in new[] { "", ".exe", "../steam", "C:\\Steam.exe", "a/b", "a\n.exe", ".." })
    Reject(() => ApplicationRules.Normalize([new(name, 30)]), "invalid executable name: " + name);
Reject(() => ApplicationRules.Normalize([new("steam", 30), new("Steam.exe", 5)]), "duplicate budgets rejected");
foreach (var rate in new[] { double.NaN, double.PositiveInfinity, 0, -.1, .09, 100001 })
    Reject(() => ApplicationRules.Validate([new("steam", rate)]), "invalid rate");
Reject(() => ApplicationRules.Validate([new("steam")]), "empty rule rejected");
Reject(() => ApplicationRules.Normalize(Enumerable.Range(0, 33).Select(i => new ApplicationRule("app" + i, 1))), "rule count bounded");
Check(ApplicationRules.Normalize([new("steam", null, 5, false)])[0] is { Enabled: false, DownloadMbps: null, UploadMbps: 5 }, "disabled upload-only persists");
var parsed = ControlProtocol.ParseCommand("{\"mbps\":null,\"applicationRules\":[{\"processName\":\"Steam.EXE\",\"downloadMbps\":30,\"uploadMbps\":5}]}");
Check(parsed.ApplicationRules is { Length: 0 } && parsed.Mbps is null && parsed.UploadMbps is null, "IPC validates but discards obsolete rules and caps");
foreach (var json in new[] {
    "[{\"processName\":\"steam\"}]", "[{\"processName\":\"C:/steam\",\"downloadMbps\":1}]",
    "[{\"processName\":\"steam\",\"downloadMbps\":0}]",
    "[{\"processName\":\"steam\",\"downloadMbps\":1},{\"processName\":\"Steam.exe\",\"uploadMbps\":2}]", "{}" })
    Reject(() => ControlProtocol.ParseCommand("{\"mbps\":null,\"applicationRules\":" + json + "}"), "IPC rejects invalid rules");
Check(ControlProtocol.ParseCommand("{\"mbps\":null}").ApplicationRules?.Length == 0, "legacy IPC no rules");
var flow = new SocketFlow(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.2"), 51000, IPAddress.Parse("198.51.100.1"), 443);
var started = DateTimeOffset.UtcNow.AddMinutes(-1);
var snapshot = OwnershipSnapshot.Create([new(flow, 42, started, "steam")]);
var policy = new ApplicationRulePolicy(() => snapshot);
var steam = new ApplicationLimit("steam", .1, .1);
policy.Configure([steam]);
using (var packet = Packet(10, PacketDirection.Inbound)) Check(policy.Classify(packet) == steam, "fresh matching identity");
var firstFlow = flow;
var secondFlow = flow with { LocalPort = 51001 };
snapshot = OwnershipSnapshot.Create([new(firstFlow, 42, started, "steam"), new(secondFlow, 43, started.AddSeconds(1), "STEAM")]);
flow = secondFlow;
using (var packet = Packet(10, PacketDirection.Inbound)) Check(ReferenceEquals(policy.Classify(packet), steam), "second PID/start identity resolves to same shared executable budget");
flow = firstFlow;
snapshot = OwnershipSnapshot.Create([new(flow, 42, started.AddSeconds(1), "other")]);
using (var packet = Packet(10, PacketDirection.Inbound)) Check(policy.Classify(packet) is null, "reused PID changes rule owner");
snapshot = OwnershipSnapshot.Create([new(flow, 42, null, "steam")]);
using (var packet = Packet(10, PacketDirection.Inbound)) Check(policy.Classify(packet) is null, "missing creation identity bypasses");
snapshot = OwnershipSnapshot.Create([new(flow, 42, started, "steam")], DateTimeOffset.UtcNow.AddSeconds(-2));
using (var packet = Packet(10, PacketDirection.Inbound)) Check(policy.Classify(packet) is null, "stale owner bypasses");
snapshot = OwnershipSnapshot.Create([new(flow, 42, started, "steam"), new(flow, 43, started, "steam")]);
using (var packet = Packet(10, PacketDirection.Inbound)) Check(policy.Classify(packet) is null, "ambiguous same-name ownership bypasses");
snapshot = OwnershipSnapshot.Create([new(flow, 42, started, "steam")]);
using (var packet = Packet(0, PacketDirection.Outbound)) Check(policy.Classify(packet) is null, "pure acknowledgements stay free");
policy.Configure([]);
using (var packet = Packet(10, PacketDirection.Inbound)) Check(policy.Classify(packet) is null, "removed rule bypasses");
policy.Configure([steam]);
var internetFlow = flow;
flow = flow with { RemoteAddress = IPAddress.Parse("192.168.1.3") };
snapshot = OwnershipSnapshot.Create([new(flow, 42, started, "steam")]);
using (var packet = Packet(10, PacketDirection.Inbound)) Check(policy.Classify(packet) is null, "explicit rules preserve LAN bypass");
flow = internetFlow;

// Legacy caps cannot create queues or call removed shaping classifiers.
foreach (var priority in new[] { PacketPriority.None, PacketPriority.Game, PacketPriority.Voice })
using (var device = new Device())
{
    var legacy = new ApplicationLimit("steam", .1, .1);
    var obsoleteCalls = 0;
    await using var pump = new PacketPump(device, .1,
        shouldLimit: _ => { obsoleteCalls++; throw new IOException("obsolete download classifier"); },
        priority: _ => priority, uploadMbps: .1,
        shouldLimitUpload: _ => { obsoleteCalls++; throw new IOException("obsolete upload classifier"); },
        applicationRule: _ => { obsoleteCalls++; throw new IOException("obsolete app classifier"); },
        applicationRules: [legacy]);
    for (var i = 0; i < 4; i++) device.Push(Raw(1, 10000));
    device.Push(Raw(2, 10000, PacketDirection.Outbound));
    await device.WaitCount(5);
    Check(!pump.HasApplicationRules && !pump.HasDownloadLimit && !pump.HasUploadLimit,
        "no legacy cap can create shaping lanes: " + priority);
    Check(obsoleteCalls == 0 && pump.Failure is null && pump.DroppedPackets == 0,
        "obsolete classifiers never run and all packets pass: " + priority);
    Check(device.Sent.Count == 5 && device.Sent.First().Marker == 1,
        "forwarding retains arrival order without a delayed app lane");
    pump.UpdateLimit(.1); pump.UpdateUploadLimit(.1); pump.UpdateApplicationRules([legacy]);
    device.Push(Raw(3, 10000)); await device.WaitCount(6);
    Check(!pump.HasApplicationRules && pump.LimitedDownloadBytes == 0,
        "legacy updates never restore app limits or shared-budget demand");
}
foreach (var classifierFailure in new[] { false, true })
using (var device = new Device { ThrowOnSend = !classifierFailure })
{
    await using var pump = new PacketPump(device, null,
        priority: _ => classifierFailure ? throw new IOException("injected priority failure") : PacketPriority.None);
    device.Push(Raw(1, 1500));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (pump.Failure is null) await Task.Delay(5, timeout.Token);
    await pump.DisposeAsync();
    Check(!pump.Active && device.Disposed && pump.Failure is IOException,
        "priority and send errors still release capture: " + classifierFailure);
}
// Exercise the real helper command loop with memory streams and a fake device.
// Obsolete app-only commands must not start capture without QoS/observation.
{
    var leftToRight = Channel.CreateUnbounded<byte[]>(); var rightToLeft = Channel.CreateUnbounded<byte[]>();
    using var desktop = new MemoryDuplex(rightToLeft, leftToRight);
    using var helperStream = new MemoryDuplex(leftToRight, rightToLeft);
    using var commands = new StreamWriter(desktop, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    using var replies = new StreamReader(desktop, leaveOpen: true);
    using var helperInput = new StreamReader(helperStream, leaveOpen: true);
    using var helperOutput = new StreamWriter(helperStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    using var stop = new CancellationTokenSource();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    using var parent = Process.GetCurrentProcess();
    Device? current = null; var created = 0;
    Func<IPacketDevice> factory = () => { created++; return current = new Device(); };
    var method = typeof(ApplicationLimit).Assembly.GetType("Dynex.Shaper.Program")!.GetMethod("RunConnectedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
    var helper = (Task<int>)method.Invoke(null, [parent, helperInput, helperOutput, stop, factory])!;
    await replies.ReadLineAsync(timeout.Token);
    const string downRule = "{\"mbps\":null,\"applicationRules\":[{\"processName\":\"steam\",\"downloadMbps\":30}]}";
    for (var i = 0; i < 2; i++)
    {
        await commands.WriteLineAsync(downRule);
        using var status = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!);
        Check(created == 0 && current is null && status.RootElement.GetProperty("captureReleased").GetBoolean(), "obsolete app-only rule cannot start capture");
    }
    await commands.WriteLineAsync("{\"mbps\":1,\"uploadMbps\":0.1,\"qosEnabled\":true,\"applicationRules\":[{\"processName\":\"steam\",\"downloadMbps\":0.1}]}");
    using (var status = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!))
        Check(created == 1 && current is { Disposed: false } &&
            !status.RootElement.GetProperty("active").GetBoolean() &&
            !status.RootElement.GetProperty("uploadActive").GetBoolean() &&
            !status.RootElement.GetProperty("applicationRulesActive").GetBoolean(),
            "QoS capture discards all obsolete cap fields at the helper boundary");
    await commands.WriteLineAsync("{\"mbps\":null,\"commandId\":9}");
    using (var status = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!))
        Check(current is { Disposed: true } && status.RootElement.GetProperty("captureReleased").GetBoolean(), "removing final app rule releases real helper session");
    await commands.WriteLineAsync("{\"mbps\":null,\"shutdown\":true}");
    while (true)
    {
        using var status = JsonDocument.Parse((await replies.ReadLineAsync(timeout.Token))!);
        if (status.RootElement.GetProperty("stopped").GetBoolean()) break;
    }
    Check(await helper.WaitAsync(timeout.Token) == 0, "app-only helper clean shutdown");
}
Console.WriteLine($"PASS {count} application-rule checks (isolated packet device)");

CapturedPacket Packet(int payload, PacketDirection direction)
{
    var p = new CapturedPacket(ArrayPool<byte>.Shared.Rent(40 + payload), 40 + payload) { Direction = direction };
    var b = p.Buffer.AsSpan(0, p.Length); b.Clear(); b[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(b[2..], (ushort)p.Length); b[9] = 6;
    var outbound = direction == PacketDirection.Outbound;
    (outbound ? flow.LocalAddress : flow.RemoteAddress).GetAddressBytes().CopyTo(b[12..]);
    (outbound ? flow.RemoteAddress : flow.LocalAddress).GetAddressBytes().CopyTo(b[16..]);
    BinaryPrimitives.WriteUInt16BigEndian(b[20..], outbound ? flow.LocalPort : flow.RemotePort);
    BinaryPrimitives.WriteUInt16BigEndian(b[22..], outbound ? flow.RemotePort : flow.LocalPort); b[32] = 0x50;
    return p;
}
static CapturedPacket Raw(byte marker, int bytes, PacketDirection direction = PacketDirection.Inbound)
{ var p = new CapturedPacket(ArrayPool<byte>.Shared.Rent(bytes), bytes) { Direction = direction }; p.Buffer[0] = marker; return p; }
sealed class Device : IPacketDevice
{
    public bool ThrowOnSend { get; init; }
    public bool Disposed { get; private set; }
    private readonly BlockingCollection<CapturedPacket> incoming = new();
    internal readonly ConcurrentQueue<(byte Marker, long At)> Sent = new();
    internal void Push(CapturedPacket packet) => incoming.Add(packet);
    public CapturedPacket Receive() => incoming.Take();
    public void Send(CapturedPacket packet)
    { if (ThrowOnSend) throw new IOException("injected send failure"); Sent.Enqueue((packet.Buffer[0], Stopwatch.GetTimestamp())); }
    internal async Task WaitCount(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (Sent.Count < count) await Task.Delay(5, timeout.Token);
    }
    public void Dispose() { Disposed = true; incoming.CompleteAdding(); while (incoming.TryTake(out var p)) p.Dispose(); }
}
sealed class MemoryDuplex(Channel<byte[]> incoming, Channel<byte[]> outgoing) : Stream
{
    private byte[] pending = []; private int offset;
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset == pending.Length)
        { if (!await incoming.Reader.WaitToReadAsync(cancellationToken)) return 0; pending = await incoming.Reader.ReadAsync(cancellationToken); offset = 0; }
        var count = Math.Min(buffer.Length, pending.Length - offset);
        pending.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => outgoing.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { outgoing.Writer.TryComplete(); base.Dispose(disposing); }
}
