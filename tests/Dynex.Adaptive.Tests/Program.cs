using Dynex.Core;

var checks = 0;
var failures = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) { failures.Add(name); Console.Error.WriteLine("FAIL: " + name); return; }
    checks++;
    Console.WriteLine("PASS: " + name);
}
void Near(double? actual, double expected, string name) => Check(actual is { } value && Math.Abs(value - expected) < .02, name + $" (actual {actual}, expected {expected})");
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
Near(decision.DownloadLimitMbps, 60, "repeated measurements cannot retrigger reduction and their stale hold eventually releases");
var restarted = new LimitPlanner(settings);
var firstBoot = Guid.NewGuid();
var secondBoot = Guid.NewGuid();
for (var i = 0; i < 3; i++) Sample(restarted, i * 5, Gamer(i * 5, 100 + i) with { SessionId = firstBoot });
for (var i = 0; i < 3; i++) decision = Sample(restarted, 12 + i * 5, Gamer(12 + i * 5, i + 1) with { SessionId = firstBoot });
Near(decision.DownloadLimitMbps, 51, "lower sequences in the same authenticated peer session cannot count as new measurements");
for (var i = 0; i < 3; i++) decision = Sample(restarted, 30 + i * 5, Gamer(30 + i * 5, i + 1) with { SessionId = secondBoot });
Near(decision.DownloadLimitMbps, 57, "a new session cannot validate the prior session reduction; bounded release starts before another experiment");
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
Near(decision.DownloadLimitMbps, 36.66, "missing gamer feedback cannot retain an unverified reduction indefinitely");
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
Near(decision.DownloadLimitMbps, 60, "repeated recovery measurement cannot confirm efficacy and the unverified hold releases");
var bothRecovering = Reduced(true);
for (var i = 0; i < 5; i++) decision = Sample(bothRecovering, 30 + i * 5,
    Gamer(30 + i * 5, i + 4, 21), Gamer(30 + i * 5, 4, 21, secondId));
Near(decision.DownloadLimitMbps, 36.66, "partial fresh recovery cannot confirm efficacy and the unverified hold releases");
for (var i = 0; i < 5; i++) decision = Sample(bothRecovering, 55 + i * 5,
    Gamer(55 + i * 5, i + 9, 21), Gamer(55 + i * 5, i + 5, 21, secondId));
Near(decision.DownloadLimitMbps, 36.66, "healthy gamers after bounded stale release cannot exceed the assigned share");
var changedTarget = new LimitPlanner(settings);
Sample(changedTarget, 0, Gamer(0, 1)); Sample(changedTarget, 5, Gamer(5, 2));
decision = Sample(changedTarget, 10, Gamer(10, 3) with { LatencyTargetId = "external-B|route-B" });
Near(decision.DownloadLimitMbps, 60, "peer target change discards accumulated evidence");

var expiry = Reduced();
decision = Sample(expiry, 31 * 60, Gamer(31 * 60, 4, 21));
Near(decision.DownloadLimitMbps, 57, "expired gamer baseline starts bounded safety release rather than retaining its old reduction forever");
Check(expiry.Plan(false, [], now: origin.AddMinutes(32)).DownloadLimitMbps is null, "ending gaming immediately frees automatic limit");
Check(!expiry.Plan(false, [], now: origin.AddMinutes(32)).AdaptiveReduced, "ending gaming clears adaptive state");
Near(Reduced().Plan(true, [Gamer(12, 4)], Local(12), origin.AddSeconds(12)).DownloadLimitMbps, 46.75,
    "starting local gaming preserves an existing reduction for background downloads");

var floor = new LimitPlanner(settings);
for (var i = 0; i < 20 * 3; i++) decision = Sample(floor, i * 5, Gamer(i * 5, i + 1));
Near(decision.DownloadLimitMbps, 60, "persistent unchanged gamer latency restores the ineffective experiment instead of ratcheting to ten percent");

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
Near(decision.DownloadLimitMbps, 55, "unloaded high local latency cannot retain an ineffective experiment indefinitely");

var bothHealthy = LocallyReducedWithHealthyPeer();
for (var i = 0; i < 5; i++)
{
    var t = 30 + i * 5;
    decision = bothHealthy.Plan(true, [Gamer(t, i + 4, 21)], Local(t, 0, 21), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 55, "healthy unloaded gamers finish bounded release without exceeding the assigned share");
var repeatedLocal = LocallyReducedWithHealthyPeer();
for (var i = 0; i < 5; i++)
{
    var t = 30 + i * 5;
    decision = repeatedLocal.Plan(true, [Gamer(t, i + 4, 21)], Local(30, 0, 21), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 55, "stale local gamer evidence releases its old experiment despite fresh remote rounds");

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
// A manual maximum is the real starting budget, even when the shared allocation is larger.
var manualSettings = settings with { ManualDownloadLimitMbps = 25 };
LimitPlanner ManualIdleBaseline()
{
    var p = new LimitPlanner(manualSettings);
    for (var i = 0; i < 5; i++) p.Plan(false, [], Local(-10 + i * 2, 2), origin.AddSeconds(-10 + i * 2));
    return p;
}
var manualLocal = ManualIdleBaseline();
for (var i = 0; i < 3; i++)
{
    var t = i * 5;
    decision = manualLocal.Plan(true, [], Local(t, 24, 90), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 21.25, "local congestion at a manual maximum reduces that maximum immediately");
Check(decision.AdaptiveReduced && decision.SharedDownloadBudgetMbps == 120,
    "manual local adaptation preserves the common download pool metadata");
for (var i = 0; i < 3; i++)
{
    var t = 26 + i * 2;
    decision = manualLocal.Plan(true, [], Local(t, 2, 21), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 23.75, "local recovery gradually restores the manual maximum");
for (var i = 0; i < 3; i++)
{
    var t = 46 + i * 2;
    decision = manualLocal.Plan(true, [], Local(t, 2, 21), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 25, "local recovery cannot exceed the manual maximum");
Check(!decision.AdaptiveReduced, "full manual local recovery clears the reduction state");

var manualPeer = new LimitPlanner(manualSettings);
for (var i = 0; i < 3; i++)
{
    var t = i * 5;
    decision = manualPeer.Plan(false, [Gamer(t, i + 1)], Local(t, 24), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 21.25, "peer congestion at a manual maximum reduces that maximum immediately");
Check(decision.AdaptiveReduced && decision.SharedDownloadBudgetMbps == 120,
    "manual peer adaptation preserves the common download pool metadata");
var manualReset = new LimitPlanner(manualSettings);
for (var i = 0; i < 3; i++)
{
    var t = i * 5;
    manualReset.Plan(false, [Gamer(t, i + 1)], Local(t, 24), origin.AddSeconds(t));
}
var endedManual = manualReset.Plan(false, [], now: origin.AddSeconds(12));
Check(endedManual.DownloadLimitMbps == 25 && !endedManual.AdaptiveReduced,
    "ending gaming clears an active adaptive reduction while preserving the manual maximum");
Near(new LimitPlanner(settings with { ManualDownloadLimitMbps = 100 }).Plan(false, [Gamer(0, 1)], Local(0, 24), origin).DownloadLimitMbps,
    60, "a manual maximum above the allocation cannot increase the allocated share");
for (var i = 0; i < 5; i++)
{
    var t = 30 + i * 5;
    decision = manualPeer.Plan(false, [Gamer(t, i + 4, 21)], Local(t, 2), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 25, "healthy low-load rounds finish bounded release without exceeding the manual maximum");
for (var i = 0; i < 15; i++)
{
    var t = 55 + i * 5;
    decision = manualPeer.Plan(false, [Gamer(t, i + 9, 21)], Local(t, 2), origin.AddSeconds(t));
}
Near(decision.DownloadLimitMbps, 25, "peer recovery cannot exceed the manual maximum");
Check(!decision.AdaptiveReduced, "full manual peer recovery clears the reduction state");
Near(manualPeer.Plan(false, [], now: origin.AddSeconds(140)).DownloadLimitMbps, 25,
    "ending gaming preserves the configured manual maximum");

foreach (var peerFeedback in new[] { false, true })
{
    var lowLoad = ManualIdleBaseline();
    var noBaseline = new LimitPlanner(manualSettings);
    for (var i = 0; i < 3; i++)
    {
        var t = i * 5;
        var peers = peerFeedback ? new[] { Gamer(t, i + 1) } : [];
        decision = lowLoad.Plan(!peerFeedback, peers, Local(t, 2, 90), origin.AddSeconds(t));
        noBaseline.Plan(!peerFeedback, peers.Select(p => p with { IdleBaselineMs = null }).ToArray(),
            Local(t, 24, 90), origin.AddSeconds(t));
    }
    Check(decision.DownloadLimitMbps == 25 && !decision.AdaptiveReduced,
        $"manual {(peerFeedback ? "peer" : "local")} adaptation still requires sufficient own load");
    var noBaselineDecision = noBaseline.Plan(!peerFeedback,
        peerFeedback ? [Gamer(12, 4) with { IdleBaselineMs = null }] : [], Local(12, 24, 90), origin.AddSeconds(12));
    Check(noBaselineDecision.DownloadLimitMbps == 25 && !noBaselineDecision.AdaptiveReduced,
        $"manual {(peerFeedback ? "peer" : "local")} adaptation still requires a valid baseline");
}
// Each consumer adapts only its assigned share of the automatic measured-capacity budget.
var commonSettings = settings;
for (var downloaders = 1; downloaders <= 3; downloaders++)
{
    var ids = Enumerable.Range(0, downloaders).Select(_ => Guid.NewGuid()).ToArray();
    var plans = new List<LimitDecision>();
    foreach (var id in ids)
    {
        var adaptive = new LimitPlanner(commonSettings with { DeviceId = id });
        for (var sample = 0; sample < 3; sample++)
        {
            var t = sample * 5;
            var peers = ids.Where(other => other != id).Select(other => new PeerAdvertisement(other, "Downloader", false,
                80, 20, origin.AddSeconds(t), ShareWeight: 1, LimitedDownloadMbps: 120)).Append(
                Gamer(t, sample + 1) with { ShareWeight = 1, LimitedDownloadMbps = 0 }).ToArray();
            decision = adaptive.Plan(false, peers, Local(t, 80) with { LimitedDownloadMbps = 120 }, origin.AddSeconds(t));
        }
        plans.Add(decision);
    }
    Check(plans.All(p => p.AdaptiveReduced && p.SharedDownloadBudgetMbps == 120 && p.DownloadParticipantCount == downloaders),
        $"{downloaders} consumers adapt allocated shares without modifying or duplicating the automatic measured-capacity budget");
    Check(plans.Sum(p => p.DownloadLimitMbps) <= 102 && plans.All(p => p.DownloadLimitMbps <= 120d / downloaders),
        $"{downloaders} adaptive consumers cannot overdraw the 120 Mbps automatic pool");
}
var gameOnlyLoad = new LimitPlanner(commonSettings);
for (var sample = 0; sample < 3; sample++)
{
    var t = sample * 5;
    decision = gameOnlyLoad.Plan(false, [Gamer(t, sample + 1) with { ShareWeight = 1, LimitedDownloadMbps = 0 }],
        Local(t, 115) with { LimitedDownloadMbps = 0 }, origin.AddSeconds(t));
}
Check(!decision.AdaptiveReduced && decision.DownloadLimitMbps == 120,
    "known zero classified bulk overrides high interface/game traffic as adaptation load evidence");
ReductionEfficacyChecks.Run(Check);
Console.WriteLine($"{checks} adaptive checks passed; pure policy, no sockets, router or shaper changes.");

if (failures.Count != 0) { Console.Error.WriteLine($"{failures.Count} checks failed."); Environment.ExitCode = 1; }
