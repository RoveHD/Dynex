using Dynex.Core;

namespace Dynex.Mobile;

/// <summary>A planned consumer share, never a measured capacity or confirmation of an applied shaper limit.</summary>
public sealed record MobileLimitDecision(double? DownloadLimitMbps, bool GamingActive, string Reason,
    double? SharedDownloadBudgetMbps = null, double? SharedCapacityMbps = null, int ParticipantCount = 1,
    int GamingCount = 0, bool SharedBudgetComplete = false, double ShareWeightTotal = 1, double LocalShareWeight = 1,
    double? SharedUploadCapacityMbps = null, int DownloadParticipantCount = 1,
    bool DownloadActivityComplete = false, double DownloadShareWeightTotal = 1);

/// <summary>
/// Consumer-only planning from AutoLanPeerService.Snapshot(): the caller must supply
/// authenticated peers, keep its own CapacityMeasured=false and always publish IsGaming=false.
/// Negotiated capacity/budget must never be re-advertised as a local measurement.
/// </summary>
public static class MobileLimitPolicy
{
    public static MobileLimitDecision Plan(Guid deviceId, IReadOnlyList<PeerAdvertisement> peers,
        bool autoConnect = true, bool paused = false, double? gamingDownloadLimitMbps = null,
        double shareWeight = 1, TimeSpan? peerTtl = null, DateTimeOffset? now = null, ProbeStats? localDownload = null)
    {
        ArgumentNullException.ThrowIfNull(peers);
        if (deviceId == Guid.Empty) throw new ArgumentException("Eine gültige lokale Geräte-ID ist erforderlich.", nameof(deviceId));
        if (!ValidWeight(shareWeight)) throw new ArgumentOutOfRangeException(nameof(shareWeight));
        if (gamingDownloadLimitMbps is { } limit && (!double.IsFinite(limit) || limit is < .1 or > 100000))
            throw new ArgumentOutOfRangeException(nameof(gamingDownloadLimitMbps));
        var ttl = peerTtl ?? TimeSpan.FromSeconds(10);
        if (ttl < TimeSpan.FromSeconds(1) || ttl > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(peerTtl));
        if (!autoConnect) return new(null, false, "Automatische PC-Verbindung ausgeschaltet · kein Limit");
        var at = now ?? DateTimeOffset.UtcNow;
        // Match Core's freshness, duplicate identity and membership rules. The
        // handset is an additional participant and never contributes a gamer.
        var live = peers.Where(peer => peer.DeviceId != Guid.Empty && peer.DeviceId != deviceId &&
                at - peer.LastSeenUtc < ttl && peer.LastSeenUtc <= at.AddSeconds(5))
            .GroupBy(peer => peer.DeviceId).Select(group => group.OrderByDescending(peer => peer.LastSeenUtc).First()).ToArray();
        // Keep one allocator for desktop and mobile: activity is classified bulk
        // ingress, never total NIC traffic or the peer's gaming flag.
        var core = new LimitPlanner(new DynexSettings
        {
            DeviceId = deviceId, CapacityMeasured = false, AdaptiveEnabled = false,
            ManualDownloadLimitMbps = gamingDownloadLimitMbps, ShareWeight = shareWeight, PeerTtl = ttl,
            HeartbeatInterval = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(2).Ticks, ttl.Ticks / 2))
        }).Plan(false, live, localDownload, at);
        var pool = core.SharedDownloadBudgetMbps;
        var downloadWeight = core.DownloadShareWeightTotal ?? core.ShareWeightTotal ?? core.ParticipantCount;
        // Current consumers share the measured PC pool without inventing capacity.
        // Legacy peers can still establish a capacity pool via their original proposals.
        var complete = core.SharedBudgetComplete || live.Length != 0 && live.All(peer => peer.BudgetProposal is { IsValid: true }) &&
                (pool is null || pool * core.LocalShareWeight / downloadWeight >= .1);
        MobileLimitDecision Decision(double? rate, string reason) => new(rate, core.GamingActive, reason,
            pool, core.SharedCapacityMbps, core.ParticipantCount, core.GamingCount, complete,
            core.ShareWeightTotal ?? core.ParticipantCount, core.LocalShareWeight, core.SharedUploadCapacityMbps,
            core.DownloadParticipantCount, core.DownloadActivityComplete, downloadWeight);
        if (paused) return Decision(null, "Pausiert · kein Limit");
        if (!core.GamingActive) return Decision(null, "Kein Gaming auf verbundenen PCs · kein Limit");
        if (pool is null) return Decision(null, "Gaming erkannt · keine bestätigte PC-Kapazität · kein Limit");
        var reason = complete ? "Gaming auf verbundenem PC · gemeinsames Downloadbudget geteilt"
            : "Gaming auf verbundenem PC · gemeinsames Downloadbudget noch unvollständig";
        if (gamingDownloadLimitMbps.HasValue) reason += " · manuelle Obergrenze";
        return Decision(core.DownloadLimitMbps, reason);
    }

    private static bool ValidWeight(double weight) => double.IsFinite(weight) && weight is >= .25 and <= 4;
}
