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
Console.WriteLine($"{checks} mobile consumer policy checks passed; no network, sockets or shaper started.");
