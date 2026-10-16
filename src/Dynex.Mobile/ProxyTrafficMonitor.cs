namespace Dynex.Mobile;

public readonly record struct ProxyTrafficStatus(double? DownloadMbps, double? UploadMbps,
    bool ForwardingObserved, bool LimitApplied, double? LimitedDownloadMbps = null);

/// <summary>
/// Forwarding/shaping evidence comes only from forwarded SOCKS payload. Classified
/// bulk activity uses a separate lifetime ingress counter before pacing; a live
/// counter can measure idle without claiming forwarding. DNS/LAN bypass never counts.
/// One instance belongs to one proxy/TUN lifetime and must be reset on replacement.
/// </summary>
public sealed class ProxyTrafficMonitor(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromSeconds(12);
    private ProxySnapshot? previous;
    private long previousAt, forwardingAt, limitedAt;
    private bool forwarding, limited;

    public void Reset()
    {
        previous = null;
        previousAt = forwardingAt = limitedAt = 0;
        forwarding = limited = false;
    }

    public ProxyTrafficStatus Observe(ProxySnapshot? sample, double? plannedMbps)
    {
        if (sample is not { } current) { Reset(); return default; }
        var now = clock.GetTimestamp();
        var prior = previous;
        if (prior is { } old && (current.DownloadBytes < old.DownloadBytes || current.UploadBytes < old.UploadBytes ||
                current.BulkDownloadIngressBytes < old.BulkDownloadIngressBytes))
        { Reset(); prior = null; }
        double? down = null, up = null, bulk = null;
        if (prior is { BulkDownloadIngressBytes: { } previousBulk } && current.BulkDownloadIngressBytes is { } currentBulk)
        {
            var seconds = clock.GetElapsedTime(previousAt, now).TotalSeconds;
            // This counter observes actual classified ingress even when no cap is
            // installed. A live, idle data plane is zero; absent/legacy is unknown.
            if (seconds is > 0 and <= 10)
                bulk = Math.Min(100000, (currentBulk - previousBulk) * 8d / seconds / 1_000_000);
        }
        var downloaded = current.DownloadBytes > (prior?.DownloadBytes ?? 0);
        var uploaded = current.UploadBytes > (prior?.UploadBytes ?? 0);
        if (downloaded || uploaded) { forwarding = true; forwardingAt = now; }
        var fresh = forwarding && clock.GetElapsedTime(forwardingAt, now) < EvidenceLifetime;
        if (prior is { } baseline && fresh)
        {
            var seconds = clock.GetElapsedTime(previousAt, now).TotalSeconds;
            if (seconds > 0)
            {
                down = Math.Min(100000, (current.DownloadBytes - baseline.DownloadBytes) * 8d / seconds / 1_000_000);
                up = Math.Min(100000, (current.UploadBytes - baseline.UploadBytes) * 8d / seconds / 1_000_000);
            }
        }
        var sameLimit = prior?.LimitVersion == current.LimitVersion;
        if (!sameLimit) limited = false;
        if (current.LimitedDownloadBytes > (sameLimit ? prior!.Value.LimitedDownloadBytes : 0))
        { limited = true; limitedAt = now; }
        var matchingLimit = plannedMbps is > 0 && current.DownloadLimitBytesPerSecond == (long)(plannedMbps.Value * 125000);
        var applied = matchingLimit && limited && clock.GetElapsedTime(limitedAt, now) < EvidenceLifetime;
        previous = current;
        previousAt = now;
        return new(down, up, fresh, applied, bulk);
    }
}
