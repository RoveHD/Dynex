using System.Net;
using System.Net.Sockets;
using System.Text;
using Dynex.Core;

try
{
if (OperatingSystem.IsWindows()) TestErrorMode.SuppressDialogs();
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
void Near(double? actual, double expected, string name) => Check(actual is { } d && Math.Abs(d - expected) < 0.02, name);
if (args.Contains("--peer-connections"))
{
    await PeerConnectionTests.Run(Check);
    Console.WriteLine($"{checks} peer connection checks passed; UDP integration ran on loopback only.");
    return;
}
var key = GroupKey.Generate();
var settings = new DynexSettings { GroupKeyBase64 = key };
var now = DateTimeOffset.UtcNow;
PeerAdvertisement Gamer(Guid? id = null) => new(id ?? Guid.NewGuid(), "Gaming PC", true, null, null, now);
var gamer = Gamer();
var planner = new LimitPlanner(settings);
Near(planner.Plan(false, new[] { gamer }, now: now).DownloadLimitMbps, 79, "200 Mbps minus headroom/gaming reserve is shared by both PCs");
Near(planner.Plan(false, new[] { gamer, Gamer() }, now: now).DownloadLimitMbps, 48.66, "two gamers reserve bandwidth and all three PCs share the remaining budget");
var downloader = new PeerAdvertisement(Guid.NewGuid(), "Download PC", false, 40, null, now);
Near(planner.Plan(false, new[] { gamer, downloader }, now: now).DownloadLimitMbps, 52.66, "gaming and non-gaming PCs all share the remaining download budget");
Near(planner.Plan(true, new[] { gamer }, now: now).DownloadLimitMbps, 73, "local gaming keeps background downloads limited while game packets bypass separately");
var steamWhileGaming = new LimitPlanner(settings).Plan(true, Array.Empty<PeerAdvertisement>(), now: now);
Near(steamWhileGaming.DownloadLimitMbps, 158, "local gaming plus Steam/download traffic receives a finite background budget even alone");
Check(steamWhileGaming.Reason.Contains("Spiele frei", StringComparison.Ordinal), "policy reason distinguishes background-download limit from game-packet bypass");
var participants = new[]
{
    new PeerAdvertisement(settings.DeviceId, "Gaming A", true, null, null, now),
    gamer with { DeviceName = "Gaming B" }, downloader
};
var combined = participants.Sum(p => new LimitPlanner(settings with { DeviceId = p.DeviceId }).Plan(p.IsGaming,
    participants.Where(other => other.DeviceId != p.DeviceId).ToArray(), now: now).DownloadLimitMbps!.Value);
Check(combined <= 146 && combined > 145, "gaming PCs downloading too cannot make summed download budgets exceed the shared reserve");
Check(planner.Plan(false, Array.Empty<PeerAdvertisement>(), now: now).DownloadLimitMbps is null, "no gaming peer releases the automatic limit");
Check(planner.Plan(false, new[] { gamer with { LastSeenUtc = now.AddSeconds(-11) } }, now: now).DownloadLimitMbps is null, "planner also rejects stale peer snapshots");
Near(planner.Plan(false, new[] { gamer, gamer }, now: now).DownloadLimitMbps, 79, "duplicate device advertisements cannot skew the budget");
var manual = new LimitPlanner(settings with { ManualDownloadLimitMbps = 25 });
Near(manual.Plan(true, new[] { gamer }, now: now).DownloadLimitMbps, 25, "explicit manual download cap also applies to local gaming");
Near(manual.Plan(false, Array.Empty<PeerAdvertisement>(), now: now).DownloadLimitMbps, 25, "manual cap survives peer loss by user intent");

LimitPlanner BaselinePlanner(bool adaptive = true)
{
    var p = new LimitPlanner(settings with { AdaptiveEnabled = adaptive });
    for (var i = 0; i < 5; i++)
    {
        var t = now.AddSeconds(i * 2);
        p.Plan(false, Array.Empty<PeerAdvertisement>(), new(t, 20, 3, true, "target-A"), t);
    }
    return p;
}
PeerAdvertisement FreshGamer(DateTimeOffset at) => gamer with { LastSeenUtc = at };
var adaptive = BaselinePlanner();
LimitDecision? decision = null;
for (var i = 0; i < 3; i++)
{
    var at = now.AddSeconds(10 + i * 2);
    decision = adaptive.Plan(false, new[] { FreshGamer(at) }, new(at, 60, 155, true, "target-A"), at);
}
Near(decision!.IdleBaselineMs, 20, "reliable idle baseline requires five measurements over eight seconds");
Near(decision.DownloadLimitMbps, 67.15, "sustained external latency plus load reduces the budget");
Check(decision.AdaptiveReduced, "adaptive decision reports reduced state");
var expiring = BaselinePlanner();
for (var i = 0; i < 3; i++)
{
    var at = now.AddSeconds(10 + i * 2);
    decision = expiring.Plan(false, new[] { FreshGamer(at) }, new(at, 60, 155, true, "target-A"), at);
}
Check(decision!.AdaptiveReduced, "baseline expiry regression starts from an actually reduced cap");
var afterExpiry = now.AddMinutes(31);
decision = expiring.Plan(false, new[] { FreshGamer(afterExpiry) }, null, afterExpiry);
Check(decision.IdleBaselineMs is null && !decision.AdaptiveReduced, "baseline expiry clears prior adaptive reduction even without new probes");
Near(decision.DownloadLimitMbps, 79, "expired baseline restores the static gaming budget");
var repeated = now.AddSeconds(16);
for (var i = 0; i < 8; i++) decision = adaptive.Plan(false, new[] { FreshGamer(repeated) }, new(repeated, 60, 155, true, "target-A"), repeated);
Near(decision!.DownloadLimitMbps, 67.15, "repeated probe timestamps cannot retrigger adjustment");
for (var i = 0; i < 3; i++)
{
    var at = now.AddSeconds(26 + i * 2);
    decision = adaptive.Plan(false, new[] { FreshGamer(at) }, new(at, 21, 130, true, "target-A"), at);
}
Near(decision!.DownloadLimitMbps, 75.05, "recovery uses hysteresis and gradual release");
var targetChanged = now.AddSeconds(32);
decision = adaptive.Plan(false, new[] { FreshGamer(targetChanged) }, new(targetChanged, 70, 155, true, "target-B"), targetChanged);
Check(decision.IdleBaselineMs is null && !decision.AdaptiveReduced, "changing the probe target invalidates the baseline");
var missing = BaselinePlanner();
foreach (var sample in new[] { new ProbeStats(now.AddSeconds(10), null, 155), new(now.AddSeconds(12), 80, null), new(now.AddSeconds(14), 80, 155, false) })
    decision = missing.Plan(false, new[] { FreshGamer(sample.TimestampUtc) }, sample, sample.TimestampUtc);
Check(!decision!.AdaptiveReduced, "missing and unrelated metrics never count as congestion");
var unloaded = BaselinePlanner();
for (var i = 0; i < 8; i++)
{
    var at = now.AddSeconds(10 + i * 2);
    decision = unloaded.Plan(false, new[] { FreshGamer(at) }, new(at, 80, 3, true, "target-A"), at);
}
Check(!decision!.AdaptiveReduced, "elevated latency without local download load does not reduce limits");
var disabled = BaselinePlanner(false);
Check(disabled.Plan(false, new[] { gamer }, new(now, 80, 155), now).IdleBaselineMs is null, "adaptive switch disables baseline and adaptation");
var zeroLatency = new LimitPlanner(settings);
for (var i = 0; i < 5; i++)
{
    var at = now.AddSeconds(i * 2);
    decision = zeroLatency.Plan(false, Array.Empty<PeerAdvertisement>(), new(at, 0, 1), at);
}
Near(decision!.IdleBaselineMs, 0, "zero millisecond RTT is a valid idle baseline");

using var codec = new PacketCodec(key);
var wire = new WireAdvertisement(Guid.NewGuid(), Guid.NewGuid(), 2, now.ToUnixTimeMilliseconds(), "Secret device", true, 12, null, 50, "Example Game", true);
var packet = codec.Encode(wire);
Check(codec.Decode(packet) == wire, "AES-GCM / HMAC packet roundtrip");
Check(!Encoding.UTF8.GetString(packet).Contains("Secret device", StringComparison.Ordinal), "device metadata is encrypted");
using var foreign = new PacketCodec(GroupKey.Generate());
Check(foreign.Decode(packet) is null, "foreign group keys are rejected");
for (var i = 0; i < packet.Length; i++)
{
    var mutated = packet.ToArray(); mutated[i] ^= 1;
    if (codec.Decode(mutated) is not null) throw new InvalidOperationException("Tampering accepted at byte " + i);
}
Check(true, "tampering at every packet byte is rejected");
Check(codec.Decode(new byte[2048]) is null && codec.Decode(packet.AsSpan(0, 20)) is null, "oversize and truncated packets are bounded and rejected");
Check(codec.Decode(codec.Encode(wire with { DownloadMbps = -1 })) is null, "authenticated invalid metrics are rejected");
Check(codec.Decode(codec.Encode(wire with { UploadMbps = -1 })) is null &&
    codec.Decode(codec.Encode(wire with { UploadMbps = 100001 })) is null, "authenticated invalid upload metrics are rejected");
Check(codec.Decode(codec.Encode(wire with { UploadMbps = 0 }))?.UploadMbps == 0, "zero upload is distinct from a missing upload metric");
Check(codec.Decode(codec.Encode(wire with { UploadMbps = null }))?.UploadMbps is null, "legacy heartbeat without upload decodes as unknown");
Check(codec.Decode(codec.Encode(wire with { BudgetProposal = null }))?.BudgetProposal is null,
    "legacy PSK heartbeat without a budget proposal stays compatible and explicitly unknown");
foreach (var invalidBudget in new[] { new DownloadBudgetProposal(0, 15, 12), new(200, 91, 12), new(200, 15, -1) })
    Check(codec.Decode(codec.Encode(wire with { BudgetProposal = invalidBudget })) is null,
        "PSK rejects authenticated invalid shared-budget capacity or reserve settings");
Check(codec.Decode(codec.Encode(wire with { DeviceName = "bad\nname" })) is null, "authenticated control characters in names are rejected");
var unicode = wire with { DeviceName = new string('界', 80), Game = new string('界', 80) };
Check(codec.Decode(codec.Encode(unicode)) == unicode, "maximum-length Unicode names fit the bounded encrypted heartbeat");
var fullFeedback = unicode with { IdleBaselineMs = 20, LatencyTargetId = new string('x', 256), LatencySampleSequence = 1,
    IdleBaselineAtUtc = now, BudgetProposal = new(100000, 90, 100000) };
Check(codec.Decode(codec.Encode(fullFeedback)) == fullFeedback, "PSK maximum legal Unicode metadata plus full latency feedback fits the bounded packet");
Check(codec.Decode(codec.Encode(wire with { IdleBaselineMs = -1 })) is null &&
    codec.Decode(codec.Encode(wire with { LatencySampleSequence = 0 })) is null &&
    codec.Decode(codec.Encode(wire with { LatencyTargetId = "bad\nroute" })) is null,
    "PSK rejects invalid baseline metrics, feedback sequences and target control characters");
Check(!LanAddress.IsAllowed(IPAddress.Parse("8.8.8.8"), false) && !LanAddress.IsAllowed(IPAddress.Loopback, false) &&
    LanAddress.IsAllowed(IPAddress.Parse("192.168.1.2"), false), "LAN scope rejects public and production loopback sources");
try { _ = new PeerService(settings with { GroupKeyBase64 = "invalid" }); Check(false, "invalid key"); }
catch (ArgumentException) { Check(true, "invalid group keys fail before opening sockets"); }
try { _ = new PeerService(settings with { DeviceName = "bad\nname" }); Check(false, "control character name"); }
catch (ArgumentException) { Check(true, "control characters in local device names fail before opening sockets"); }

int FreePort()
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.LocalEndPoint!).Port;
}
async Task Until(Func<bool> predicate, string description, int timeoutMs = 4000)
{
    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
    while (!predicate())
    {
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(description);
        await Task.Delay(25);
    }
}
var portA = FreePort();
var portB = FreePort();
while (portB == portA) portB = FreePort();
var aSettings = settings with { Port = portA, LoopbackOnly = true, DeviceId = Guid.NewGuid(), DeviceName = "PC A",
    DirectPeers = new[] { new IPEndPoint(IPAddress.Loopback, portB) }, PeerTtl = TimeSpan.FromSeconds(1), HeartbeatInterval = TimeSpan.FromMilliseconds(100) };
var bSettings = aSettings with { DeviceId = Guid.NewGuid(), DeviceName = "PC B", Port = portB,
    DirectPeers = new[] { new IPEndPoint(IPAddress.Loopback, portA) } };
await using var a = new PeerService(aSettings);
await using var b = new PeerService(bSettings);
b.Publish(true, 10, 22, null, "Test game", true, 20, "target-route", now, 0);
await a.StartAsync();
await b.StartAsync();
try { await Until(() => a.Snapshot().Count == 1 && b.Snapshot().Count == 1, "two UDP peers discover each other"); }
catch { Console.Error.WriteLine($"UDP diagnostic: A={a.Snapshot().Count}, B={b.Snapshot().Count}, A error={a.NetworkStatus ?? "none"}, B error={b.NetworkStatus ?? "none"}"); throw; }
Check(a.Snapshot()[0].IsGaming && a.Snapshot()[0].Game == "Test game" && a.Snapshot()[0].ShaperActive, "two real UDP loopback peers exchange authenticated status");
Check(a.Snapshot()[0].IdleBaselineMs == 20 && a.Snapshot()[0].LatencyTargetId == "target-route" && a.Snapshot()[0].IdleBaselineAtUtc == now,
    "PSK UDP peer publishes timestamped gamer latency feedback");
Check(a.Snapshot()[0].UploadMbps == 0, "PSK UDP peer publishes zero upload distinctly");
Check(a.Snapshot()[0].BudgetProposal == DownloadBudgetProposal.FromSettings(bSettings),
    "PSK encrypted status advertises original local capacity and reserve settings");
try { b.Publish(false, uploadMbps: double.NaN); Check(false, "invalid upload rejected on publish"); }
catch (ArgumentException) { Check(true, "PSK rejects non-finite upload values before publishing"); }
b.Publish(true, 10, 22, null, "Test game", true, 20, "target-route", now, 15);
await Until(() => a.Snapshot()[0].UploadMbps == 15, "PSK upload update");
Check(a.Snapshot()[0].UploadMbps == 15, "PSK peer receives updated upload rate");
b.Publish(true, 10, 22, null, "Test game", true, 20, "target-route", now, null);
await Until(() => a.Snapshot()[0].UploadMbps is null, "PSK missing upload update");
Check(a.Snapshot()[0].UploadMbps is null, "PSK peer receives null upload updates");
b.Publish(true, 10, 22, null, "Test game", true, 20, "target-route", now, 15);
await Until(() => a.Snapshot()[0].UploadMbps == 15, "PSK throughput refresh before expiry");
var pskThroughputSequence = a.Snapshot()[0].LatencySampleSequence;
await Until(() => a.Snapshot()[0].DownloadMbps is null && a.Snapshot()[0].UploadMbps is null, "PSK published throughput expires after peer TTL", 2500);
Check(a.Snapshot()[0].LatencySampleSequence == pskThroughputSequence, "PSK throughput expiry preserves the published latency sample ID");
Check(a.Snapshot()[0].BudgetProposal is null, "PSK heartbeat stops advertising a budget proposal after Publish goes stale");
b.Publish(true, 10, 22, null, "Test game", true, 20, "target-route", now, 15);
await Until(() => a.Snapshot()[0].UploadMbps == 15, "PSK throughput resumes after fresh publish");
var pskSampleSequence = a.Snapshot()[0].LatencySampleSequence;
var pskSeenAt = a.Snapshot()[0].LastSeenUtc;
await Until(() => a.Snapshot()[0].LastSeenUtc > pskSeenAt, "PSK next heartbeat");
Check(a.Snapshot()[0].LatencySampleSequence == pskSampleSequence, "PSK repeat heartbeats preserve the original published sample sequence");
Check(a.NetworkStatus is null && b.NetworkStatus is null, "loopback transport reports no socket errors");
var netPlanner = new LimitPlanner(aSettings);
Check(netPlanner.Plan(false, a.Snapshot()).DownloadLimitMbps is not null, "real received gaming status activates planning");
await b.StopAsync();
await Until(() => a.Snapshot().Count == 0, "peer expires after stop");
Check(netPlanner.Plan(false, a.Snapshot()).DownloadLimitMbps is null, "real UDP peer loss expires and releases the automatic cap");
await b.StartAsync();
await Until(() => a.Snapshot().Count == 1, "peer restarts");
Check(a.Snapshot()[0].IsGaming, "stopped service can restart without replay regression");
await b.StopAsync();
await Until(() => a.Snapshot().Count == 0, "peer expires before replay test");
using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
async Task Send(byte[] bytes) => await sender.SendToAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, portA));
wire = wire with { SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
await Send(codec.Encode(wire));
await Until(() => a.Snapshot().Any(p => p.DeviceId == wire.DeviceId), "synthetic authenticated peer accepted");
await Send(codec.Encode(wire with { Sequence = 1, DeviceName = "REPLAYED", IsGaming = false }));
await Send(codec.Encode(wire with { DeviceName = "DUPLICATED", IsGaming = false }));
await Task.Delay(120);
Check(a.Snapshot().Single().DeviceName == "Secret device" && a.Snapshot().Single().IsGaming, "live receiver rejects duplicate and out-of-order sequence numbers");
await Until(() => a.Snapshot().Count == 0, "replay peer TTL");
await Send(codec.Encode(wire));
await Task.Delay(120);
Check(a.Snapshot().Count == 0, "replay history survives peer TTL expiration");
await Send(foreign.Encode(wire with { Sequence = 3 }));
await Send(codec.Encode(wire with { Sequence = 3, SentAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds() }));
await Send(new byte[2048]);
await Task.Delay(120);
Check(a.Snapshot().Count == 0, "foreign groups, stale packets and oversized datagrams never create peers");
await Send(codec.Encode(wire with { Sequence = 4, SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
await Until(() => a.Snapshot().Count == 1, "receiver survives invalid datagrams");
Check(true, "receiver continues after invalid and oversized datagrams");
var nextSession = wire with { SessionId = Guid.NewGuid(), Sequence = 1, DeviceName = "New session", SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
await Send(codec.Encode(nextSession));
await Until(() => a.Snapshot().SingleOrDefault()?.DeviceName == "New session", "new boot session accepted");
await Until(() => a.Snapshot().Count == 0, "new boot session expires");
await Send(codec.Encode(wire with { Sequence = 5, SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
await Task.Delay(120);
Check(a.Snapshot().Count == 0, "retired boot sessions stay rejected after the current peer TTL expires");
for (var i = 0; i < 70; i++)
    await Send(codec.Encode(wire with { DeviceId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Sequence = 1,
        SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
await Until(() => a.Snapshot().Count == 64, "bounded peer table fills");
Check(a.Snapshot().Count == 64, "authenticated peer flooding cannot exceed the 64-peer table bound");
await a.StopAsync();
Check(a.Snapshot().Count == 0, "stop immediately clears remote state");
// A fresh gaming advertisement must omit unmeasured default capacity as well.
var unknownPort = FreePort();
var observerPort = FreePort();
while (observerPort == unknownPort) observerPort = FreePort();
await using (var unknown = new PeerService(aSettings with
{
    CapacityMeasured = false, Port = unknownPort, DeviceId = Guid.NewGuid(),
    DirectPeers = [new(IPAddress.Loopback, observerPort)]
}))
await using (var observer = new PeerService(aSettings with
{
    Port = observerPort, DeviceId = Guid.NewGuid(), DirectPeers = []
}))
{
    unknown.Publish(true, 1, game: "Unmeasured game");
    await observer.StartAsync(); await unknown.StartAsync();
    await Until(() => observer.Snapshot().Count == 1, "unmeasured peer received");
    Check(observer.Snapshot().Single() is { IsGaming: true, DownloadMbps: 1, BudgetProposal: null },
        "fresh unmeasured PSK peer publishes gaming and throughput without a default budget proposal");
}
await AutoLanTests.Run(Check);
await PeerConnectionTests.Run(Check);
await SharedBudgetTests.Run(Check);
Console.WriteLine($"{checks} checks passed; UDP integration ran on loopback only.");
}
catch (Exception error)
{
    // A failed assertion is a normal nonzero test result, never an unhandled
    // Windows application crash dialog on the user's desktop.
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

internal static class TestErrorMode
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
    internal static void SuppressDialogs() => SetErrorMode(GetErrorMode() | 0x8003);
}
