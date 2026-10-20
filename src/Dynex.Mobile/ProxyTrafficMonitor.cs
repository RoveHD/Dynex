namespace Dynex.Mobile;

public readonly record struct ProxyTrafficStatus(double? DownloadMbps, double? UploadMbps,
    bool ForwardingObserved, bool LimitApplied, double? LimitedDownloadMbps = null);

/// <summary>
/// Forwarding evidence comes only from forwarded SOCKS payload. Public download
/// activity uses a separate lifetime ingress counter; a live
/// counter can measure idle without claiming forwarding. DNS/LAN bypass never counts.
/// One instance belongs to one proxy/TUN lifetime and must be reset on replacement.
/// </summary>
public sealed class ProxyTrafficMonitor(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromSeconds(12);
    private ProxySnapshot? previous;
    private long previousAt, forwardingAt;
    private bool forwarding;

    public void Reset()
    {
        previous = null;
        previousAt = forwardingAt = 0;
        forwarding = false;
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
        // Legacy limit fields cannot establish an applied cap in the priority-only app.
        previous = current;
        previousAt = now;
        return new(down, up, fresh, false, bulk);
    }
}
