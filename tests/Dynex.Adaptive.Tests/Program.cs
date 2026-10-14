using Dynex.Core;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
void Near(double? actual, double expected, string name) => Check(actual is { } value && Math.Abs(value - expected) < .02, name);
var origin = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
var settings = new DynexSettings { LineMbps = 200, HeadroomPercent = 35, GamingReserveMbps = 10 };
var gamerId = Guid.NewGuid();
var secondId = Guid.NewGuid();
PeerAdvertisement Gamer(double seconds, long sequence, double? latency = 90, Guid? id = null) =>
    new(id ?? gamerId, "Gaming PC", true, 1, latency, origin.AddSeconds(seconds),
        IdleBaselineMs: 20, LatencyTargetId: "external-A|route-A", LatencySampleSequence: sequence, IdleBaselineAtUtc: origin.AddSeconds(-2));
ProbeStats Local(double seconds, double? load = 115, double? latency = 20) =>
    new(origin.AddSeconds(seconds), latency, load, true, "downloader-proxy");
LimitDecision Sample(LimitPlanner planner, double seconds, params PeerAdvertisement[] peers) =>
    planner.Plan(false, peers, Local(seconds), origin.AddSeconds(seconds));
LimitPlanner Reduced(bool twoGamers = false)
{
    var planner = new LimitPlanner(settings);
    for (var i = 0; i < 3; i++)
    {
        var t = i * 5;
        if (twoGamers) Sample(planner, t, Gamer(t, i + 1), Gamer(t, i + 1, 21, secondId));
        else Sample(planner, t, Gamer(t, i + 1));
    }
    return planner;
}

var primary = new LimitPlanner(settings);
Near(Sample(primary, 0, Gamer(0, 1)).DownloadLimitMbps, 60, "65% background budget minus gaming reserve");
Near(Sample(primary, 5, Gamer(5, 2)).DownloadLimitMbps, 60, "one or two elevated gamer observations cannot reduce cap");
var decision = Sample(primary, 10, Gamer(10, 3));
Near(decision.DownloadLimitMbps, 51, "gamer 20 to 90 ms reduces cap even when downloader ping stays at 20 ms");
Check(decision.AdaptiveReduced, "real gamer feedback reports adaptive reduction");

var repeated = Reduced();
for (var t = 12; t <= 60; t += 2) decision = Sample(repeated, t, Gamer(t, 3));
Near(decision.DownloadLimitMbps, 51, "fresh heartbeats repeating the same published measurement cannot retrigger reduction");
var restarted = new LimitPlanner(settings);
var firstBoot = Guid.NewGuid();
var secondBoot = Guid.NewGuid();
for (var i = 0; i < 3; i++) Sample(restarted, i * 5, Gamer(i * 5, 100 + i) with { SessionId = firstBoot });
for (var i = 0; i < 3; i++) decision = Sample(restarted, 12 + i * 5, Gamer(12 + i * 5, i + 1) with { SessionId = firstBoot });
Near(decision.DownloadLimitMbps, 51, "lower sequences in the same authenticated peer session cannot count as new measurements");
for (var i = 0; i < 3; i++) decision = Sample(restarted, 30 + i * 5, Gamer(30 + i * 5, i + 1) with { SessionId = secondBoot });
Near(decision.DownloadLimitMbps, 43.35, "new authenticated session accepts restarted sample sequence with unchanged baseline and target");
var noSequence = new LimitPlanner(settings);
var fixedPeer = Gamer(0, 1) with { LatencySampleSequence = null };
for (var t = 0; t <= 8; t += 2) decision = Sample(noSequence, t, fixedPeer);
Near(decision.DownloadLimitMbps, 60, "legacy sequence-free snapshots deduplicate by LastSeenUtc");

var shortWindow = new LimitPlanner(settings);
for (var i = 0; i < 3; i++) Sample(shortWindow, i * 2, Gamer(i * 2, i + 1));
Near(Sample(shortWindow, 10, Gamer(4, 3)).DownloadLimitMbps, 60, "elapsed wall time cannot turn an old third sample into ten seconds of evidence");
Near(Sample(shortWindow, 12, Gamer(12, 4)).DownloadLimitMbps, 51, "new elevated sample after ten-second evidence window permits reduction");
var gap = new LimitPlanner(settings);
Sample(gap, 0, Gamer(0, 1)); Sample(gap, 5, Gamer(5, 2));
Near(Sample(gap, 20, Gamer(20, 3)).DownloadLimitMbps, 60, "a measurement gap longer than peer TTL restarts congestion evidence");

foreach (var invalid in new[]
{
    Gamer(0, 1) with { IdleBaselineMs = null },
    Gamer(0, 1) with { IdleBaselineMs = -1 },
    Gamer(0, 1) with { IdleBaselineMs = double.NaN },
    Gamer(0, 1) with { IdleBaselineAtUtc = null },
    Gamer(0, 1) with { IdleBaselineAtUtc = origin.AddMinutes(-31) },
    Gamer(0, 1) with { IdleBaselineAtUtc = origin.AddMinutes(5) },
    Gamer(0, 1) with { LatencyTargetId = "" },
    Gamer(0, 1) with { LatencyTargetId = "bad\nroute" },
    Gamer(0, 1) with { LatencyMs = null },
    Gamer(0, 1) with { LatencyMs = -1 },
    Gamer(0, 1) with { LatencyMs = double.NaN },
    Gamer(0, 1) with { LatencySampleSequence = 0 }
})
{
    var invalidPlanner = new LimitPlanner(settings);
    for (var i = 0; i < 3; i++)
    {
        var t = i * 5;
        decision = Sample(invalidPlanner, t, invalid with { LastSeenUtc = origin.AddSeconds(t), LatencySampleSequence = invalid.LatencySampleSequence == 0 ? 0 : i + 1 });
    }
    Check(!decision.AdaptiveReduced, "missing, invalid or expired peer evidence never infers congestion");
}
var stale = new LimitPlanner(settings);
Check(Sample(stale, 20, Gamer(0, 1)).DownloadLimitMbps is null, "expired gaming peer immediately releases automatic cap");
Check(Sample(new LimitPlanner(settings), 0, Gamer(0, 1, id: Guid.Empty)).DownloadLimitMbps is null,
    "empty peer identity cannot activate automatic gaming policy");
var zero = new LimitPlanner(settings);
for (var i = 0; i < 3; i++) decision = Sample(zero, i * 5, Gamer(i * 5, i + 1, 20) with { IdleBaselineMs = 0 });
Near(decision.DownloadLimitMbps, 51, "zero-millisecond gamer baseline is valid");

var unloaded = new LimitPlanner(settings);
for (var i = 0; i < 3; i++)
{
    var t = i * 5;
    decision = unloaded.Plan(false, [Gamer(t, i + 1)], Local(t, 30), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 60, "gamer latency without sufficient own download load does not reduce cap");
var staleLoad = new LimitPlanner(settings);
for (var i = 0; i < 3; i++)
{
    var t = i * 5;
    decision = staleLoad.Plan(false, [Gamer(t, i + 1)], Local(t - 11), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 60, "stale local load cannot establish causality");

var multi = Reduced(true);
Near(Sample(multi, 12, Gamer(12, 3), Gamer(12, 3, 21, secondId)).DownloadLimitMbps, 31.16, "worst gamer controls adaptation across multiple gaming PCs");
for (var i = 0; i < 10; i++)
{
    var t = 30 + i * 5;
    decision = Sample(multi, t, Gamer(t, i + 4, 21), Gamer(t, i + 4, null, secondId));
}
Near(decision.DownloadLimitMbps, 31.16, "healthy gamer plus missing gamer feedback cannot release reserve");
var partiallyKnown = new LimitPlanner(settings);
for (var i = 0; i < 3; i++) decision = Sample(partiallyKnown, i * 5, Gamer(i * 5, i + 1), Gamer(i * 5, i + 1, null, secondId));
Near(decision.DownloadLimitMbps, 31.16, "missing second gamer does not prevent protecting a gamer with proven congestion");

var recovering = Reduced();
for (var i = 0; i < 4; i++) decision = Sample(recovering, 30 + i * 5, Gamer(30 + i * 5, i + 4, 21));
Near(decision.DownloadLimitMbps, 51, "four recovered rounds cannot release reserve");
decision = Sample(recovering, 50, Gamer(50, 8, 21));
Near(decision.DownloadLimitMbps, 54, "five complete recovered rounds over twenty seconds increase cap gradually");
Check(decision.AdaptiveReduced, "first recovery step remains below static budget");
var staleRecovery = Reduced();
Sample(staleRecovery, 30, Gamer(30, 4, 21));
for (var t = 35; t <= 70; t += 5) decision = Sample(staleRecovery, t, Gamer(t, 4, 21));
Near(decision.DownloadLimitMbps, 51, "repeated recovery measurement cannot provide five new rounds");
var bothRecovering = Reduced(true);
for (var i = 0; i < 5; i++) decision = Sample(bothRecovering, 30 + i * 5,
    Gamer(30 + i * 5, i + 4, 21), Gamer(30 + i * 5, 4, 21, secondId));
Near(decision.DownloadLimitMbps, 31.16, "fresh recovery samples from one gamer cannot replace repeated sample of another gamer");
for (var i = 0; i < 5; i++) decision = Sample(bothRecovering, 55 + i * 5,
    Gamer(55 + i * 5, i + 9, 21), Gamer(55 + i * 5, i + 5, 21, secondId));
Near(decision.DownloadLimitMbps, 33, "all gamers supplying five genuinely new recovered rounds permit a shared step up");
var changedTarget = new LimitPlanner(settings);
Sample(changedTarget, 0, Gamer(0, 1)); Sample(changedTarget, 5, Gamer(5, 2));
decision = Sample(changedTarget, 10, Gamer(10, 3) with { LatencyTargetId = "external-B|route-B" });
Near(decision.DownloadLimitMbps, 60, "peer target change discards accumulated evidence");

var expiry = Reduced();
decision = Sample(expiry, 31 * 60, Gamer(31 * 60, 4, 21));
Near(decision.DownloadLimitMbps, 51, "expired gamer baseline cannot be mistaken for recovery");
Check(expiry.Plan(false, [], now: origin.AddMinutes(32)).DownloadLimitMbps is null, "ending gaming immediately frees automatic limit");
Check(!expiry.Plan(false, [], now: origin.AddMinutes(32)).AdaptiveReduced, "ending gaming clears adaptive state");
Near(Reduced().Plan(true, [Gamer(12, 4)], Local(12), origin.AddSeconds(12)).DownloadLimitMbps, 46.75,
    "starting local gaming preserves an existing reduction for background downloads");

var floor = new LimitPlanner(settings);
for (var i = 0; i < 20 * 3; i++) decision = Sample(floor, i * 5, Gamer(i * 5, i + 1));
Near(decision.DownloadLimitMbps, 6, "persistent gamer congestion can reach ten-percent floor while downloads continue");

var fallback = new LimitPlanner(settings);
for (var i = 0; i < 5; i++) fallback.Plan(false, [], Local(-10 + i * 2, 2), origin.AddSeconds(-10 + i * 2));
for (var i = 0; i < 3; i++)
{
    var t = i * 2;
    decision = fallback.Plan(false, [Gamer(t, i + 1) with { IdleBaselineMs = null }], Local(t, 115, 90), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 51, "existing local proxy remains fallback when no gamer baseline is available");
Check(decision.IdleBaselineAtUtc == origin.AddSeconds(-2), "decision publishes the actual local baseline timestamp");
var suppressFallback = new LimitPlanner(settings);
for (var i = 0; i < 5; i++) suppressFallback.Plan(false, [], Local(-10 + i * 2, 2), origin.AddSeconds(-10 + i * 2));
for (var i = 0; i < 3; i++)
{
    var t = i * 5;
    decision = suppressFallback.Plan(false, [Gamer(t, i + 1, null)], Local(t, 115, 90), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 60, "valid gamer baseline with missing current RTT suppresses unrelated downloader proxy inference");

LimitPlanner LocalIdleBaseline()
{
    var p = new LimitPlanner(settings);
    for (var i = 0; i < 5; i++) p.Plan(false, [], Local(-10 + i * 2, 2), origin.AddSeconds(-10 + i * 2));
    return p;
}
var soloGameDownload = LocalIdleBaseline();
Near(soloGameDownload.Plan(true, [], Local(0), origin).DownloadLimitMbps, 120,
    "a solo gaming PC still receives a finite budget for Steam and other background downloads");
for (var i = 0; i < 3; i++)
{
    var t = 2 + i * 2;
    decision = soloGameDownload.Plan(true, [], Local(t, 115, 90), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 102, "solo local-gamer latency from 20 to 90 ms adapts its background download cap");
Check(decision.AdaptiveReduced, "local gaming no longer clears its adaptive factor each cycle");
Near(soloGameDownload.Plan(true, [], Local(8, 115, 90), origin.AddSeconds(8)).DownloadLimitMbps, 102,
    "ongoing local gaming preserves the previous reduction between adjustment intervals");

LimitPlanner LocallyReducedWithHealthyPeer()
{
    var p = LocalIdleBaseline();
    for (var i = 0; i < 3; i++)
    {
        var t = i * 5;
        p.Plan(true, [Gamer(t, i + 1, 21)], Local(t, 115, 90), origin.AddSeconds(t));
    }
    return p;
}
var localWins = LocallyReducedWithHealthyPeer();
Near(localWins.Plan(true, [Gamer(12, 4, 21)], Local(12, 0, 90), origin.AddSeconds(12)).DownloadLimitMbps, 46.75,
    "a healthy remote baseline cannot mask elevated latency of the local gamer");
for (var i = 0; i < 5; i++)
{
    var t = 30 + i * 5;
    decision = localWins.Plan(true, [Gamer(t, i + 5, 21)], Local(t, 0, 90), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 46.75, "five healthy remote rounds cannot release reserve while the local gamer remains slow");

var bothHealthy = LocallyReducedWithHealthyPeer();
for (var i = 0; i < 5; i++)
{
    var t = 30 + i * 5;
    decision = bothHealthy.Plan(true, [Gamer(t, i + 4, 21)], Local(t, 0, 21), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 49.5, "both local and remote gamers recovering over five fresh rounds permit one conservative release");
var repeatedLocal = LocallyReducedWithHealthyPeer();
for (var i = 0; i < 5; i++)
{
    var t = 30 + i * 5;
    decision = repeatedLocal.Plan(true, [Gamer(t, i + 4, 21)], Local(30, 0, 21), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 46.75, "repeated or stale local gamer probes cannot validate fresh remote recovery rounds");

var localHealthyRemoteSlow = LocalIdleBaseline();
for (var i = 0; i < 3; i++)
{
    var t = i * 5;
    decision = localHealthyRemoteSlow.Plan(true, [Gamer(t, i + 1)], Local(t, 115, 21), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 46.75, "healthy local gaming does not hide congestion on a remote gaming PC");
for (var i = 0; i < 3; i++)
{
    var t = 26 + i * 2;
    decision = localHealthyRemoteSlow.Plan(true, [Gamer(t, i + 4)], Local(t, 0, 21), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 46.75, "healthy local proxy alone cannot release an existing remote-gamer reduction");

var localNoBaseline = new LimitPlanner(settings);
for (var i = 0; i < 3; i++)
{
    var t = i * 5;
    decision = localNoBaseline.Plan(true, [Gamer(t, i + 1, 21)], Local(t, 115, 90), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 55, "local gaming without a prior idle baseline keeps the static budget rather than inventing congestion");
Check(decision.IdleBaselineMs is null, "gaming samples cannot be learned as idle baseline");
Near(new LimitPlanner(settings).Plan(true, [Gamer(0, 1)], Local(20), origin.AddSeconds(20)).DownloadLimitMbps, 120,
    "remote peer expiry cannot free background downloads while local gaming continues");
Console.WriteLine($"{checks} adaptive checks passed; pure policy, no sockets, router or shaper changes.");
