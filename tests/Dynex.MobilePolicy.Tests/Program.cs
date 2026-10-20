using Dynex.Core;
using Dynex.Mobile;

var checks = 0;
void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + reason);
    checks++; Console.WriteLine("PASS: " + reason);
}
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
void NoLimit(MobileLimitDecision decision, string label) => Check(decision.DownloadLimitMbps is null &&
    decision.SharedDownloadBudgetMbps is null && decision.SharedCapacityMbps is null &&
    decision.SharedUploadCapacityMbps is null && !decision.SharedBudgetComplete, label);

var pc = Peer();
NoLimit(Plan(pc), "gaming PC never installs a handset limit or borrowed bandwidth proposal");
Check(Plan(pc) is { GamingActive: true, GamingCount: 1, ParticipantCount: 2 }, "gaming and device membership remain visible");
NoLimit(Plan(pc, Peer(), Peer(false)), "two gaming PCs and an idle PC cannot allocate mobile bandwidth");
Check(Plan(pc, Peer(), Peer(false)) is { GamingCount: 2, ParticipantCount: 4 }, "all connected devices remain represented");
NoLimit(Plan(pc with { BudgetProposal = new(200, 15, 12, 40) }), "legacy upload proposal cannot become an active handset budget");
foreach (var proposal in new DownloadBudgetProposal?[] { null, new(1, 99, 100), new(100000, 0, 0) })
    NoLimit(Plan(pc with { BudgetProposal = proposal }), "missing, exhausted and large legacy proposals stay observational");
foreach (var maximum in new double?[] { null, .1, .126, 20, 25, 100000 })
    NoLimit(MobileLimitPolicy.Plan(handset, [pc], gamingDownloadLimitMbps: maximum, now: now),
        "legacy manual maximum is ignored: " + maximum);
foreach (var weight in new[] { .25, 1, 4 })
    NoLimit(MobileLimitPolicy.Plan(handset, [pc, Peer(false, weight: 2)], shareWeight: weight, now: now),
        "legacy participant weighting cannot restore limits: " + weight);
Check(Plan(pc with { LastSeenUtc = now.AddSeconds(-9.999) }).GamingActive, "fresh peer is usable before TTL expiry");
foreach (var stale in new[] { now.AddSeconds(-10), now.AddSeconds(-60), now.AddSeconds(5.001) })
    Check(Plan(pc with { LastSeenUtc = stale }) is { GamingActive: false, ParticipantCount: 1 },
        "expired or implausibly future peers never report gaming: " + stale);
Check(Plan(pc with { LastSeenUtc = now.AddSeconds(5) }).GamingActive, "bounded five-second future tolerance is preserved");
Check(Plan(pc, pc).ParticipantCount == 2, "duplicate identities are counted once");
Check(!Plan(pc, pc with { LastSeenUtc = now.AddMilliseconds(1), IsGaming = false }).GamingActive,
    "newest authenticated status wins for duplicate identities");
Check(Plan(pc with { DeviceId = handset }, pc with { DeviceId = Guid.Empty }) is { GamingActive: false, ParticipantCount: 1 },
    "own or missing peer identities never establish remote gaming");
Check(!MobileLimitPolicy.Plan(handset, [pc], autoConnect: false, now: now).GamingActive,
    "disabled connection never consumes remote gaming state");
NoLimit(MobileLimitPolicy.Plan(handset, [pc], paused: true, now: now), "obsolete pause input cannot restore a cap");
Check(MobileLimitPolicy.Plan(handset, [pc], paused: true, now: now).GamingActive,
    "obsolete pause input does not hide actual gaming");
foreach (var value in new[] { double.NaN, double.PositiveInfinity, -.1, 0, .099, 100001 })
    Reject(() => MobileLimitPolicy.Plan(handset, [pc], gamingDownloadLimitMbps: value), "invalid legacy cap is rejected: " + value);
foreach (var value in new[] { double.NaN, double.NegativeInfinity, 0, .24, 4.01 })
    Reject(() => MobileLimitPolicy.Plan(handset, [pc], shareWeight: value), "invalid legacy weight is rejected: " + value);
Reject(() => MobileLimitPolicy.Plan(Guid.Empty, [pc]), "missing handset identity is rejected");
Reject(() => MobileLimitPolicy.Plan(handset, [pc], peerTtl: TimeSpan.Zero), "invalid TTL is rejected");

var planner = new MobileLimitPlanner();
var session = Guid.NewGuid();
for (var seconds = 0; seconds <= 1200; seconds += 5)
{
    var at = now.AddSeconds(seconds);
    var feedback = pc with { LastSeenUtc = at, SessionId = session, LatencySampleSequence = seconds + 1,
        LatencyMs = 900, IdleBaselineMs = 15, IdleBaselineAtUtc = at, LatencyTargetId = "1.1.1.1",
        DownloadMbps = 95, LimitedDownloadMbps = 95 };
    var load = new ProbeStats(at, null, 95, LimitedDownloadMbps: 95);
    var result = planner.Plan(handset, [feedback], load, gamingDownloadLimitMbps: 25, now: at);
    if (seconds % 60 == 0) NoLimit(result, "sustained bad latency and saturated load never lower mobile speed: " + seconds);
    if (!result.GamingActive) throw new InvalidOperationException("Fresh feedback must remain visible");
}
planner.Reset();
NoLimit(planner.Plan(handset, [pc], gamingDownloadLimitMbps: .1, now: now), "reset cannot re-enable legacy rate planning");
Check(!planner.Plan(handset, [pc], wifiConnected: false, now: now).GamingActive, "leaving WLAN discards active mobile peer use");
NoLimit(planner.Plan(handset, [pc], wifiConnected: false, gamingDownloadLimitMbps: .1, now: now), "cellular remains unpaced with legacy cap");
Console.WriteLine($"PASS: {checks} mobile companion checks");
