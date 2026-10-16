using Dynex.Core;

namespace Dynex.Mobile;

/// <summary>
/// Stateful consumer of authenticated PC latency feedback and measured local payload throughput.
/// Own one instance per network/peer-service lifetime; a handset never supplies gaming latency
/// or a measured capacity proposal. Core owns all congestion thresholds and recovery behavior.
/// </summary>
public sealed class MobileLimitPlanner
{
    private LimitPlanner? planner;
    private (Guid Device, double? Maximum, double Weight, TimeSpan Ttl)? configuration;
    private Dictionary<Guid, (Guid? Session, DateTimeOffset Seen)> membership = new();

    public void Reset()
    {
        planner = null;
        configuration = null;
        membership.Clear();
    }

    public MobileLimitDecision Plan(Guid deviceId, IReadOnlyList<PeerAdvertisement> peers,
        ProbeStats? localDownload = null, bool autoConnect = true, bool paused = false,
        double? gamingDownloadLimitMbps = null, double shareWeight = 1, bool wifiConnected = true,
        TimeSpan? peerTtl = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var ttl = peerTtl ?? TimeSpan.FromSeconds(10);
        // Only actual, current local load is passed through. No handset latency or
        // target is synthesized: remote gamers remain the sole latency authority.
        var load = localDownload is not null &&
            localDownload.TimestampUtc <= at.AddSeconds(1) && at - localDownload.TimestampUtc <= TimeSpan.FromSeconds(10)
            ? new ProbeStats(localDownload.TimestampUtc, null, ValidLoad(localDownload.DownloadMbps), IsGamingRelevant: false, TargetId: "",
                LimitedDownloadMbps: ValidLoad(localDownload.LimitedDownloadMbps)) : null;
        var decision = MobileLimitPolicy.Plan(deviceId, peers, autoConnect, paused,
            gamingDownloadLimitMbps, shareWeight, ttl, at, load);
        if (!wifiConnected || decision.DownloadLimitMbps is null)
        {
            Reset();
            return wifiConnected ? decision : decision with { DownloadLimitMbps = null, Reason = "Kein WLAN · kein Limit" };
        }

        var live = peers.Where(peer => peer.DeviceId != Guid.Empty && peer.DeviceId != deviceId &&
                at - peer.LastSeenUtc < ttl && peer.LastSeenUtc <= at.AddSeconds(5))
            .GroupBy(peer => peer.DeviceId).Select(group => group.OrderByDescending(peer => peer.LastSeenUtc).First()).ToArray();
        var nextConfiguration = (deviceId, gamingDownloadLimitMbps, shareWeight, ttl);
        // Heartbeats and collection order are not lifetime changes. A reauthenticated
        // session or membership gap must not inherit a previous network's reduction.
        if (configuration != nextConfiguration || membership.Count != live.Length || live.Any(peer =>
                !membership.TryGetValue(peer.DeviceId, out var prior) || prior.Session != peer.SessionId ||
                peer.LastSeenUtc - prior.Seen >= ttl))
            Reset();
        configuration = nextConfiguration;
        membership = live.ToDictionary(peer => peer.DeviceId, peer => (peer.SessionId, peer.LastSeenUtc));
        planner ??= new LimitPlanner(new DynexSettings
        {
            DeviceId = deviceId, CapacityMeasured = false, ManualDownloadLimitMbps = gamingDownloadLimitMbps,
            ShareWeight = shareWeight, PeerTtl = ttl,
            HeartbeatInterval = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(2).Ticks, ttl.Ticks / 2))
        });
        // Legacy budget participants remain visible, but only sequenced feedback
        // from a current authenticated session can drive mobile adaptation.
        var feedback = live.Select(peer => HasLatencyComparison(peer, at) ? peer : peer with { LatencyMs = null }).ToArray();
        var adaptive = planner.Plan(false, feedback, load, at);
        var result = adaptive.AdaptiveReduced && adaptive.DownloadLimitMbps is { } reduced && reduced < decision.DownloadLimitMbps
            ? decision with { DownloadLimitMbps = reduced, Reason = decision.Reason + " · Latenzreserve erhöht" }
            : decision;
        if (decision.GamingActive && !live.Any(peer => peer.IsGaming && HasLatencyComparison(peer, at)))
            result = result with { Reason = result.Reason + " · PC-Latenzvergleich fehlt" +
                (adaptive.AdaptiveReduced ? "" : " · festes Downloadbudget") };
        return result;
    }

    private static double? ValidLoad(double? rate) => rate is >= 0 and <= 100000 && double.IsFinite(rate.Value) ? rate : null;

    private static bool HasLatencyComparison(PeerAdvertisement peer, DateTimeOffset now) =>
        peer.SessionId is { } session && session != Guid.Empty && peer.LatencySampleSequence is > 0 &&
        peer.LatencyMs is >= 0 and <= 10000 && double.IsFinite(peer.LatencyMs.Value) &&
        peer.IdleBaselineMs is >= 0 and <= 10000 && double.IsFinite(peer.IdleBaselineMs.Value) &&
        !string.IsNullOrWhiteSpace(peer.LatencyTargetId) && peer.LatencyTargetId.Length <= 256 && !peer.LatencyTargetId.Any(char.IsControl) &&
        peer.IdleBaselineAtUtc is { } baselineAt && baselineAt <= now.AddSeconds(5) && now - baselineAt <= TimeSpan.FromMinutes(30);
}
