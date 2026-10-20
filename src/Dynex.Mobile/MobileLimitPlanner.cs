using Dynex.Core;

namespace Dynex.Mobile;

/// <summary>Compatibility facade for mobile status; no adaptive or fixed bandwidth planning remains.</summary>
public sealed class MobileLimitPlanner
{
    public void Reset() { }

    public MobileLimitDecision Plan(Guid deviceId, IReadOnlyList<PeerAdvertisement> peers,
        ProbeStats? localDownload = null, bool autoConnect = true, bool paused = false,
        double? gamingDownloadLimitMbps = null, double shareWeight = 1, bool wifiConnected = true,
        TimeSpan? peerTtl = null, DateTimeOffset? now = null) =>
        MobileLimitPolicy.Plan(deviceId, peers, autoConnect && wifiConnected, paused,
            gamingDownloadLimitMbps, shareWeight, peerTtl, now, localDownload);
}
