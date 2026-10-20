using Dynex.Core;

namespace Dynex.Mobile;

/// <summary>Compatibility result for the mobile companion; all bandwidth-limit fields stay unset.</summary>
public sealed record MobileLimitDecision(double? DownloadLimitMbps, bool GamingActive, string Reason,
    double? SharedDownloadBudgetMbps = null, double? SharedCapacityMbps = null, int ParticipantCount = 1,
    int GamingCount = 0, bool SharedBudgetComplete = false, double ShareWeightTotal = 1, double LocalShareWeight = 1,
    double? SharedUploadCapacityMbps = null, int DownloadParticipantCount = 1,
    bool DownloadActivityComplete = false, double DownloadShareWeightTotal = 1);

/// <summary>
/// Status from AutoLanPeerService.Snapshot(): the caller must supply
/// authenticated peers, keep its own CapacityMeasured=false and always publish IsGaming=false.
/// Legacy capacity and bandwidth limits are ignored; scheduling belongs to the router.
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
        if (!autoConnect) return new(null, false, "Automatische PC-Verbindung ausgeschaltet");
        var at = now ?? DateTimeOffset.UtcNow;
        // Legacy budget proposals are deliberately ignored. The handset observes
        // authenticated PC gaming state, while the router owns bandwidth scheduling.
        var live = peers.Where(peer => peer.DeviceId != Guid.Empty && peer.DeviceId != deviceId &&
                at - peer.LastSeenUtc < ttl && peer.LastSeenUtc <= at.AddSeconds(5))
            .GroupBy(peer => peer.DeviceId).Select(group => group.OrderByDescending(peer => peer.LastSeenUtc).First()).ToArray();
        var gamers = live.Count(peer => peer.IsGaming);
        return new(null, gamers != 0,
            gamers != 0 ? "Gaming auf verbundenem PC · Priorisierung erfolgt auf den PCs"
                : "Mit DYNEX-PCs verbunden · Bandbreite wird vom Router geregelt",
            ParticipantCount: live.Length + 1, GamingCount: gamers);
    }

    private static bool ValidWeight(double weight) => double.IsFinite(weight) && weight is >= .25 and <= 4;
}
