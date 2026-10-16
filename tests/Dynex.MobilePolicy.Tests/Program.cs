using Dynex.Core;
using Dynex.Mobile;

var checks = 0;
void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + reason);
    checks++; Console.WriteLine("PASS: " + reason);
}
void Near(double? value, double expected, string reason) => Check(value is { } actual && Math.Abs(actual - expected) < .000001, reason);
void Reject(Action action, string reason)
{
    try { action(); } catch (ArgumentException) { Check(true, reason); return; }
    throw new InvalidOperationException("FAIL: " + reason);
}
var handset = Guid.NewGuid();
var now = DateTimeOffset.UtcNow;
PeerAdvertisement Peer(bool gaming = true, double line = 200, double headroom = 15, double reserve = 12, double? weight = 1) =>
    new(Guid.NewGuid(), "Synthetic PC", gaming, null, null, now, BudgetProposal: new(line, headroom, reserve), ShareWeight: weight);
MobileLimitDecision Plan(params PeerAdvertisement[] peers) => MobileLimitPolicy.Plan(handset, peers, now: now);

var pc = Peer();
var uploadPc = pc with { BudgetProposal = new(200, 15, 12, 40) };
Check(Plan(uploadPc).SharedUploadCapacityMbps == 40 && Plan(pc).SharedUploadCapacityMbps is null,
    "handset displays authenticated PC upload capacity while legacy unknown upload remains null");
Check(Plan(uploadPc, Peer(false) with { BudgetProposal = new(500, 20, 10, 20) }).SharedUploadCapacityMbps == 20 &&
    Plan(uploadPc with { LastSeenUtc = now.AddSeconds(-11) }).SharedUploadCapacityMbps is null,
    "handset derives conservative upload metadata only from fresh remote original proposals");
var one = Plan(pc);
Near(one.DownloadLimitMbps, 79, "one gaming PC shares 158 Mbps remaining pool with the additional handset");
Check(one is { GamingActive: true, ParticipantCount: 2, GamingCount: 1, SharedBudgetComplete: true, SharedCapacityMbps: 200, SharedDownloadBudgetMbps: 158 },
    "consumer plan contains remote capacity and membership without inventing a local proposal");
var two = Plan(pc, Peer());
Near(two.DownloadLimitMbps, 48.66, "two gaming PCs reserve for both games and share with a third participant");
Check(two.ParticipantCount == 3 && two.GamingCount == 2, "handset never adds to gaming count");
Near(Plan(pc, Peer(false)).DownloadLimitMbps, 52.66, "non-gaming PCs still participate in the shared download budget");
var slower = Peer(false, 100, 10, 5);
Near(Plan(pc, slower).DownloadLimitMbps, 28.33, "minimum complete reserve-adjusted PC proposal selects shared pool");
Near(Plan(Peer(line: 300, headroom: 0, reserve: 100), Peer(false, 250, 0, 0)).DownloadLimitMbps, 66.66,
    "reserve-adjusted proposals are minimized before division rather than just raw link capacity");
var weighted = MobileLimitPolicy.Plan(handset, [pc, Peer(false, weight: 2)], shareWeight: .5, now: now);
Near(weighted.DownloadLimitMbps, 22.57, "handset receives its explicitly weighted share");
Check(weighted.ShareWeightTotal == 3.5 && weighted.LocalShareWeight == .5, "valid remote and local weights are preserved");
foreach (var weight in new double?[] { null, 0, 5, double.NaN, double.PositiveInfinity })
{
    var legacy = MobileLimitPolicy.Plan(handset, [pc, Peer(false, weight: weight)], shareWeight: 2, now: now);
    Near(legacy.DownloadLimitMbps, 52.66, "legacy/invalid remote weights switch all participants to equal shares: " + weight);
    Check(legacy.ShareWeightTotal == 3 && legacy.LocalShareWeight == 1, "legacy fallback does not retain the handset's preferred weight");
}
var unknown = Peer(false) with { BudgetProposal = null };
var partial = Plan(pc, unknown);
Near(partial.DownloadLimitMbps, 52.66, "unknown PC proposals do not invent capacity but their PC still counts as a participant");
Check(!partial.SharedBudgetComplete && partial.SharedCapacityMbps == 200, "partial budget is explicitly incomplete");
foreach (var proposal in new DownloadBudgetProposal?[] { null, new(double.NaN, 15, 12), new(0, 15, 12), new(200, 100, 12) })
{
    var missing = MobileLimitPolicy.Plan(handset, [pc with { BudgetProposal = proposal }], gamingDownloadLimitMbps: 20, now: now);
    Check(missing is { DownloadLimitMbps: null, GamingActive: true, SharedCapacityMbps: null, SharedDownloadBudgetMbps: null },
        "missing/invalid PC proposals never turn a manual maximum into an invented capacity");
}
Near(Plan(pc with { LastSeenUtc = now.AddSeconds(-9.999) }).DownloadLimitMbps, 79, "peer is usable just before TTL expiry");
foreach (var stale in new[] { now.AddSeconds(-10), now.AddSeconds(-11), now.AddSeconds(5.001) })
    Check(Plan(pc with { LastSeenUtc = stale }) is { DownloadLimitMbps: null, GamingActive: false, ParticipantCount: 1 },
        "expired or excessively future peer cannot activate a limit");
Check(MobileLimitPolicy.Plan(handset, [pc with { LastSeenUtc = now.AddSeconds(-1) }], peerTtl: TimeSpan.FromSeconds(1), now: now).DownloadLimitMbps is null,
    "configured peer TTL is respected");
Near(Plan(pc with { LastSeenUtc = now.AddSeconds(5) }).DownloadLimitMbps, 79, "Core's bounded five-second future tolerance is preserved");
Near(Plan(pc, pc).DownloadLimitMbps, 79, "duplicate peer identities do not alter the budget");
Check(Plan(pc, pc with { LastSeenUtc = now.AddMilliseconds(1), IsGaming = false }).DownloadLimitMbps is null,
    "newest peer status wins when a game ends");
Check(Plan(pc with { DeviceId = handset }, pc with { DeviceId = Guid.Empty }) is { DownloadLimitMbps: null, ParticipantCount: 1 },
    "own identity and empty identities never become remote gamers");
Check(MobileLimitPolicy.Plan(handset, [pc], autoConnect: false, now: now) is { DownloadLimitMbps: null, GamingActive: false },
    "disabled AutoConnect cannot activate protection from cached peers");
Check(MobileLimitPolicy.Plan(handset, [pc], paused: true, now: now) is { DownloadLimitMbps: null, GamingActive: true },
    "pause releases limit without claiming the remote game ended");
Near(MobileLimitPolicy.Plan(handset, [pc], gamingDownloadLimitMbps: 20, now: now).DownloadLimitMbps, 20,
    "manual gaming maximum can reduce the confirmed budget share");
Near(MobileLimitPolicy.Plan(handset, [pc], gamingDownloadLimitMbps: 100, now: now).DownloadLimitMbps, 79,
    "manual maximum cannot increase the confirmed budget share");
Near(MobileLimitPolicy.Plan(handset, [pc], gamingDownloadLimitMbps: .126, now: now).DownloadLimitMbps, .12,
    "fractional manual maximum rounds down without exceeding the user ceiling");
Check(MobileLimitPolicy.Plan(handset, [pc with { IsGaming = false }], gamingDownloadLimitMbps: 20, now: now).DownloadLimitMbps is null,
    "game end removes manual and automatic limits");
var exhausted = Plan(Peer(line: 10, reserve: 100));
Near(exhausted.DownloadLimitMbps, .1, "exhausted pool uses the same technical minimum as Core");
Check(!exhausted.SharedBudgetComplete && exhausted.SharedDownloadBudgetMbps == 0, "unrepresentable shared budget remains incomplete");
foreach (var value in new[] { double.NaN, double.PositiveInfinity, 0, .09, 100001 })
    Reject(() => MobileLimitPolicy.Plan(handset, [pc], gamingDownloadLimitMbps: value), "invalid manual cap is rejected: " + value);
foreach (var value in new[] { double.NaN, 0, .24, 4.01 })
    Reject(() => MobileLimitPolicy.Plan(handset, [pc], shareWeight: value), "invalid handset priority is rejected: " + value);
Reject(() => MobileLimitPolicy.Plan(Guid.Empty, [pc]), "missing handset identity is rejected");
Reject(() => MobileLimitPolicy.Plan(handset, [pc], peerTtl: TimeSpan.Zero), "invalid peer TTL is rejected");
var gamer = pc with { SessionId = Guid.NewGuid(), IdleBaselineMs = 20, LatencyMs = 90,
    LatencyTargetId = "valorant:udp:203.0.113.8:7000", IdleBaselineAtUtc = now.AddMinutes(-1), LatencySampleSequence = 1 };
PeerAdvertisement Feedback(int seconds, long? sequence = null, double latency = 90) => gamer with
{ LastSeenUtc = now.AddSeconds(seconds), LatencySampleSequence = sequence ?? seconds + 1, LatencyMs = latency };
ProbeStats Load(int seconds, double? rate = 24) => new(now.AddSeconds(seconds), null, rate, false, "");
MobileLimitDecision Adaptive(MobileLimitPlanner planner, int seconds, double? rate = 24, PeerAdvertisement? feedback = null) =>
    planner.Plan(handset, [feedback ?? Feedback(seconds)], Load(seconds, rate), gamingDownloadLimitMbps: 25, now: now.AddSeconds(seconds));
MobileLimitPlanner Reduced()
{
    var planner = new MobileLimitPlanner();
    Near(Adaptive(planner, 0).DownloadLimitMbps, 25, "first elevated PC sample keeps the configured mobile maximum");
    Near(Adaptive(planner, 5).DownloadLimitMbps, 25, "two elevated PC samples do not reduce prematurely");
    var reduced = Adaptive(planner, 10);
    Near(reduced.DownloadLimitMbps, 21.25, "three fresh PC latency samples spanning ten seconds reduce actual 25 Mbps mobile budget");
    Check(reduced.Reason.Contains("Latenzreserve erhöht"), "mobile status explains the extra latency reserve");
    return planner;
}
var adaptivePlanner = Reduced();
var mobileMetadata = Adaptive(adaptivePlanner, 11);
var fixedMetadata = MobileLimitPolicy.Plan(handset, [Feedback(11)], gamingDownloadLimitMbps: 25, now: now.AddSeconds(11));
Check(mobileMetadata with { DownloadLimitMbps = fixedMetadata.DownloadLimitMbps, Reason = fixedMetadata.Reason } == fixedMetadata,
    "adaptation preserves consumer-only PC capacity, pool, gamer count, upload metadata and budget completeness");
foreach (var seconds in new[] { 15, 20, 25, 30 })
    Near(Adaptive(adaptivePlanner, seconds, 20, Feedback(seconds, latency: 20)).DownloadLimitMbps, 21.25,
        "recovery waits for five complete fresh healthy rounds over twenty seconds: " + seconds);
Near(Adaptive(adaptivePlanner, 35, 20, Feedback(35, latency: 20)).DownloadLimitMbps, 22.5,
    "healthy PC feedback restores only five percent of the original budget per recovery window");

foreach (var rate in new double?[] { null, 0, 1, 18, double.NaN, double.PositiveInfinity })
{
    var planner = new MobileLimitPlanner();
    foreach (var seconds in new[] { 0, 5, 10, 15 })
        Near(Adaptive(planner, seconds, rate).DownloadLimitMbps, 25,
            "unknown, invalid or subthreshold actual handset load cannot justify reduction: " + rate);
}
foreach (var mode in new[] { "missing", "stale", "future" })
{
    var planner = new MobileLimitPlanner();
    foreach (var seconds in new[] { 0, 5, 10, 15 })
    {
        var at = now.AddSeconds(seconds);
        var load = mode == "missing" ? null : Load(seconds) with
            { TimestampUtc = mode == "stale" ? at.AddSeconds(-10.001) : at.AddSeconds(1.001) };
        Near(planner.Plan(handset, [Feedback(seconds)], load, gamingDownloadLimitMbps: 25, now: at).DownloadLimitMbps, 25,
            "missing/old/future measured throughput remains unknown: " + mode);
    }
}
foreach (var mode in new[] { "duplicate", "old", "missing sequence", "zero sequence", "missing session", "empty session", "missing baseline", "old baseline", "missing target" })
{
    var planner = new MobileLimitPlanner();
    foreach (var seconds in new[] { 0, 5, 10, 15 })
    {
        var feedback = Feedback(seconds) with
        {
            LatencySampleSequence = mode == "duplicate" ? 1 : mode == "old" ? 100 - seconds : mode == "missing sequence" ? null : mode == "zero sequence" ? 0 : seconds + 1,
            SessionId = mode == "missing session" ? null : mode == "empty session" ? Guid.Empty : gamer.SessionId,
            IdleBaselineMs = mode == "missing baseline" ? null : 20,
            IdleBaselineAtUtc = mode == "old baseline" ? now.AddMinutes(-31) : gamer.IdleBaselineAtUtc,
            LatencyTargetId = mode == "missing target" ? null : gamer.LatencyTargetId
        };
        Near(Adaptive(planner, seconds, feedback: feedback).DownloadLimitMbps, 25,
            "repeated/older PC feedback or missing gamer baseline cannot justify reduction: " + mode);
    }
}
Check(Adaptive(new MobileLimitPlanner(), 0, feedback: Feedback(0) with { IdleBaselineMs = null }).Reason
    .Contains("PC-Latenzvergleich fehlt · festes Downloadbudget"), "missing PC comparison is visible instead of suggesting an adaptive guarantee");
var cachedLoadPlanner = new MobileLimitPlanner();
foreach (var seconds in new[] { 0, 5, 10 })
    Near(cachedLoadPlanner.Plan(handset, [Feedback(seconds)], Load(0), gamingDownloadLimitMbps: 25, now: now.AddSeconds(seconds)).DownloadLimitMbps,
        seconds == 10 ? 21.25 : 25, "a real timestamped latest throughput measurement remains usable for at most ten seconds: " + seconds);
var nonLatencyPlanner = new MobileLimitPlanner();
foreach (var seconds in new[] { 0, 5, 10, 15, 20, 25 })
    Near(nonLatencyPlanner.Plan(handset, [Feedback(seconds) with { IdleBaselineMs = null }],
        new(now.AddSeconds(seconds), seconds < 10 ? 20 : 90, 24, true, "fake-handset-latency"),
        gamingDownloadLimitMbps: 25, now: now.AddSeconds(seconds)).DownloadLimitMbps, 25,
        "handset latency fields cannot become an invented local gamer baseline");

foreach (var mode in new[] { "no gaming", "capacity lost", "wifi lost", "pause", "autoconnect off" })
{
    var planner = Reduced();
    var feedback = Feedback(15) with { IsGaming = mode != "no gaming", BudgetProposal = mode == "capacity lost" ? null : gamer.BudgetProposal };
    Check(planner.Plan(handset, [feedback], Load(15), autoConnect: mode != "autoconnect off", paused: mode == "pause",
        gamingDownloadLimitMbps: 25, wifiConnected: mode != "wifi lost", now: now.AddSeconds(15)).DownloadLimitMbps is null,
        "mobile limit is released immediately: " + mode);
    Near(Adaptive(planner, 20).DownloadLimitMbps, 25, "released consumer protection discards old adaptive reduction: " + mode);
}
var changedPlanner = Reduced();
Near(changedPlanner.Plan(handset, [Feedback(15)], Load(15), gamingDownloadLimitMbps: 20, now: now.AddSeconds(15)).DownloadLimitMbps, 20,
    "changing the manual maximum starts a fresh adaptive state");
var sessionPlanner = Reduced();
Near(Adaptive(sessionPlanner, 15, feedback: Feedback(15) with { SessionId = Guid.NewGuid() }).DownloadLimitMbps, 25,
    "a reauthenticated PC session cannot inherit previous lifetime reduction");
var gapPlanner = Reduced();
Near(Adaptive(gapPlanner, 25).DownloadLimitMbps, 25, "a peer lifetime gap starts fresh even when identity and session are unchanged");
var resetPlanner = Reduced();
resetPlanner.Reset();
Near(Adaptive(resetPlanner, 15).DownloadLimitMbps, 25, "explicit network/peer-service reset discards old reduction");
var orderedPlanner = new MobileLimitPlanner();
var companion = Peer(false) with { SessionId = Guid.NewGuid() };
foreach (var seconds in new[] { 0, 5, 10 })
{
    var ordered = seconds == 5 ? new[] { companion with { LastSeenUtc = now.AddSeconds(seconds) }, Feedback(seconds) }
        : new[] { Feedback(seconds), companion with { LastSeenUtc = now.AddSeconds(seconds) } };
    var result = orderedPlanner.Plan(handset, ordered, Load(seconds), gamingDownloadLimitMbps: 25, now: now.AddSeconds(seconds));
    Near(result.DownloadLimitMbps, seconds == 10 ? 21.25 : 25, "peer order and ordinary heartbeats preserve the adaptive window: " + seconds);
}
var sharedGamer = pc with { BudgetProposal = new(200, 15, 12, 40, 25), LimitedDownloadMbps = 0 };
ProbeStats Bulk(double? value, int ageSeconds = 0) => new(now.AddSeconds(-ageSeconds), null, 99, false, "", value);
MobileLimitDecision Shared(ProbeStats? load, params PeerAdvertisement[] devices) =>
    MobileLimitPolicy.Plan(handset, devices, now: now, localDownload: load);
var onlyHandset = Shared(Bulk(24), sharedGamer);
Near(onlyHandset.DownloadLimitMbps, 25, "one actual handset downloader receives the complete common 25 Mbps gaming maximum");
Check(onlyHandset is { ParticipantCount: 2, DownloadParticipantCount: 1, DownloadActivityComplete: true,
    SharedDownloadBudgetMbps: 25, SharedCapacityMbps: 200, ShareWeightTotal: 2, DownloadShareWeightTotal: 1,
    SharedUploadCapacityMbps: 40, SharedBudgetComplete: true }, "idle gamer is excluded only from download allocation; upload membership and measured capacities are preserved");
Near(Shared(Bulk(24), sharedGamer with { LimitedDownloadMbps = 12 }).DownloadLimitMbps, 12.5,
    "two actual downloaders split the common 25 Mbps into 12.5 each");
var otherDownloader = Peer(false) with { LimitedDownloadMbps = 8, BudgetProposal = new(200, 15, 12, 40, 25) };
var secondHandset = otherDownloader with { BudgetProposal = null, LimitedDownloadMbps = 12 };
var twoHandsets = Shared(Bulk(12), sharedGamer, secondHandset);
Near(twoHandsets.DownloadLimitMbps, 12.5, "two downloading handsets share the measured PC's common maximum without inventing handset capacity");
Check(twoHandsets is { SharedBudgetComplete: true, DownloadActivityComplete: true, ParticipantCount: 3, DownloadParticipantCount: 2 },
    "measured PC and two activity-reporting consumer handsets confirm a complete shared maximum");
var legacyHandset = Shared(Bulk(12), sharedGamer, secondHandset with { LimitedDownloadMbps = null });
Near(legacyHandset.DownloadLimitMbps, 8.33, "unknown legacy handset activity retains a conservative three-participant share");
Check(!legacyHandset.SharedBudgetComplete && !legacyHandset.DownloadActivityComplete,
    "legacy handset with unknown activity cannot confirm complete enforcement of the shared maximum");
Near(Shared(Bulk(8), sharedGamer with { LimitedDownloadMbps = 8 }, otherDownloader).DownloadLimitMbps, 8.33,
    "three active downloaders share the same maximum with conservative rounding");
Near(Shared(Bulk(24), sharedGamer, otherDownloader with { LimitedDownloadMbps = 0 }).DownloadLimitMbps, 25,
    "two idle peers leave the full common maximum to the only active handset");
var idleHandset = Shared(Bulk(0), sharedGamer with { LimitedDownloadMbps = 25 });
Near(idleHandset.DownloadLimitMbps, 12.5, "idle handset receives a prospective starting share beside one active PC");
Check(idleHandset.DownloadParticipantCount == 2 && idleHandset.DownloadActivityComplete,
    "prospective idle share exposes its actual download denominator");
Near(Shared(Bulk(0), sharedGamer).DownloadLimitMbps, 25, "all measured idle participants retain bounded prospective starts");
foreach (var missingBulk in new double?[] { null, double.NaN, -1, double.PositiveInfinity })
{
    var fallback = Shared(Bulk(24), sharedGamer with { LimitedDownloadMbps = missingBulk });
    Near(fallback.DownloadLimitMbps, 12.5, "legacy or invalid peer bulk telemetry falls back to all participants: " + missingBulk);
    Check(!fallback.DownloadActivityComplete && !fallback.SharedBudgetComplete && fallback.DownloadShareWeightTotal == 2,
        "unknown peer activity is explicitly incomplete rather than treated as idle");
}
foreach (var load in new ProbeStats?[] { null, Bulk(null), Bulk(0, 10), Bulk(0, 11) })
{
    var fallback = Shared(load, sharedGamer);
    Near(fallback.DownloadLimitMbps, 12.5, "unknown or expired local bulk probe conservatively includes every participant");
    Check(!fallback.DownloadActivityComplete && !fallback.SharedBudgetComplete, "expired or missing local activity cannot claim a complete active set");
}
Near(Shared(Bulk(24), sharedGamer with { LimitedDownloadMbps = .05 }).DownloadLimitMbps, 25,
    "bulk activity must exceed 0.05 Mbps to count as an active downloader");
Near(Shared(Bulk(24), sharedGamer with { LimitedDownloadMbps = .051 }).DownloadLimitMbps, 12.5,
    "bulk activity above the explicit threshold joins the shared maximum");
Near(MobileLimitPolicy.Plan(handset, [sharedGamer], gamingDownloadLimitMbps: 20, now: now, localDownload: Bulk(24)).DownloadLimitMbps, 20,
    "handset manual maximum remains local and cannot increase its shared allocation");
Near(MobileLimitPolicy.Plan(handset, [sharedGamer with { LimitedDownloadMbps = 12 }], gamingDownloadLimitMbps: 20,
    now: now, localDownload: Bulk(24)).DownloadLimitMbps, 12.5, "handset local maximum does not replace the common pool");
Near(MobileLimitPolicy.Plan(handset, [sharedGamer with { LimitedDownloadMbps = 12, ShareWeight = 2 }], shareWeight: 1,
    now: now, localDownload: Bulk(24)).DownloadLimitMbps, 8.33, "active download priority uses the Core weighted allocation");
Near(Shared(Bulk(24), sharedGamer, otherDownloader with { BudgetProposal = new(200, 15, 12, 40, 10), LimitedDownloadMbps = 0 }).DownloadLimitMbps,
    10, "lowest original authenticated common maximum wins independently of an idle proposer's activity");
Check(Shared(Bulk(24), sharedGamer with { IsGaming = false }).DownloadLimitMbps is null,
    "common maximum remains gaming-only and does not create a permanent mobile limit");
foreach (var mode in new[] { "paused", "disabled", "no wifi", "capacity missing" })
{
    var gated = new MobileLimitPlanner().Plan(handset,
        [mode == "capacity missing" ? sharedGamer with { BudgetProposal = null } : sharedGamer], Bulk(24),
        paused: mode == "paused", autoConnect: mode != "disabled", wifiConnected: mode != "no wifi", now: now);
    Check(gated.DownloadLimitMbps is null, "shared maximum preserves mobile protection release gate: " + mode);
}
var sharedAdaptive = new MobileLimitPlanner();
foreach (var seconds in new[] { 0, 5, 10 })
{
    var result = sharedAdaptive.Plan(handset, [Feedback(seconds) with { BudgetProposal = sharedGamer.BudgetProposal, LimitedDownloadMbps = 0 }],
        Bulk(24) with { TimestampUtc = now.AddSeconds(seconds) }, now: now.AddSeconds(seconds));
    Near(result.DownloadLimitMbps, seconds == 10 ? 21.25 : 25, "mobile adaptation and allocation use the same current bulk-active share: " + seconds);
    Check(result.DownloadParticipantCount == 1 && result.DownloadActivityComplete,
        "stateful mobile wrapper preserves Core download activity metadata");
}
Console.WriteLine($"{checks} mobile consumer policy checks passed; no network, sockets or shaper started.");
