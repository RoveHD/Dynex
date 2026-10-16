using Dynex.Core;

namespace Dynex.Mobile;

/// <summary>A planned consumer share, never a measured capacity or confirmation of an applied shaper limit.</summary>
public sealed record MobileLimitDecision(double? DownloadLimitMbps, bool GamingActive, string Reason,
    double? SharedDownloadBudgetMbps = null, double? SharedCapacityMbps = null, int ParticipantCount = 1,
    int GamingCount = 0, bool SharedBudgetComplete = false, double ShareWeightTotal = 1, double LocalShareWeight = 1);

/// <summary>
/// Consumer-only planning from AutoLanPeerService.Snapshot(): the caller must supply
/// authenticated peers, keep its own CapacityMeasured=false and always publish IsGaming=false.
/// Negotiated capacity/budget must never be re-advertised as a local measurement.
/// </summary>
public static class MobileLimitPolicy
{
    public static MobileLimitDecision Plan(Guid deviceId, IReadOnlyList<PeerAdvertisement> peers,
        bool autoConnect = true, bool paused = false, double? gamingDownloadLimitMbps = null,
        double shareWeight = 1, TimeSpan? peerTtl = null, DateTimeOffset? now = null)
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
        var gamers = live.Count(peer => peer.IsGaming);
        var participants = live.Length + 1;
        var weighted = live.All(peer => peer.ShareWeight is { } weight && ValidWeight(weight));
        var localWeight = weighted ? shareWeight : 1;
        var totalWeight = weighted ? localWeight + live.Sum(peer => peer.ShareWeight!.Value) : participants;
        var proposals = live.Select(peer => peer.BudgetProposal).Where(proposal => proposal is { IsValid: true })
            .Cast<DownloadBudgetProposal>().ToArray();
        var capacity = proposals.Length == 0 ? (double?)null : proposals.Min(proposal => proposal.LineMbps);
        var pool = gamers == 0 || proposals.Length == 0 ? (double?)null : proposals.Min(proposal =>
            Math.Max(0, proposal.LineMbps * (1 - proposal.HeadroomPercent / 100) - gamers * proposal.GamingReserveMbps));
        var complete = proposals.Length == live.Length && proposals.Length != 0 && (pool is null || pool / participants >= .1);
        MobileLimitDecision Decision(double? rate, string reason) => new(rate, gamers > 0, reason,
            pool, capacity, participants, gamers, complete, totalWeight, localWeight);
        if (paused) return Decision(null, "Pausiert · kein Limit");
        if (gamers == 0) return Decision(null, "Kein Gaming auf verbundenen PCs · kein Limit");
        if (pool is not { } budget) return Decision(null, "Gaming erkannt · keine bestätigte PC-Kapazität · kein Limit");
        // Core rounds automatic weighted shares downward and uses a 0.1 Mbps
        // technical minimum. A manual maximum can only reduce this known share.
        var rate = Math.Floor(Math.Max(.1, budget * localWeight / totalWeight) * 100) / 100;
        if (gamingDownloadLimitMbps is { } maximum) rate = Math.Floor(Math.Min(rate, maximum) * 100) / 100;
        var reason = complete ? "Gaming auf verbundenem PC · gemeinsames Downloadbudget geteilt"
            : "Gaming auf verbundenem PC · gemeinsames Downloadbudget noch unvollständig";
        if (gamingDownloadLimitMbps.HasValue) reason += " · manuelle Obergrenze";
        return Decision(rate, reason);
    }

    private static bool ValidWeight(double weight) => double.IsFinite(weight) && weight is >= .25 and <= 4;
}
