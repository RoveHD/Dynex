namespace Dynex.Core;

/// <summary>One connected PC's current load, used to explain a ping spike.</summary>
public sealed record LoadSource(string Pc, double? DownloadMbps, double? UploadMbps, string? TopProgram = null);

/// <summary>
/// Raises one alert when the internet ping stays clearly above its recent normal while gaming,
/// and names the most likely cause in the LAN (the PC/program with the highest current load).
/// </summary>
public sealed class PingAlarm(TimeSpan? cooldown = null)
{
    private readonly TimeSpan _cooldown = cooldown ?? TimeSpan.FromMinutes(2);
    private readonly Queue<double> _normal = new();
    private int _high;
    private DateTimeOffset _lastAlert = DateTimeOffset.MinValue;

    public double? Baseline => _normal.Count >= 5 ? _normal.Order().ElementAt(_normal.Count / 2) : null;

    public string? Observe(double? pingMs, bool gaming, IReadOnlyList<LoadSource> loads, DateTimeOffset now)
    {
        if (pingMs is not { } ping || !double.IsFinite(ping) || ping < 0) return null;
        var baseline = Baseline;
        var spike = baseline is { } normal && ping > Math.Max(normal * 2, normal + 30);
        if (!spike)
        {
            _high = 0;
            _normal.Enqueue(ping);
            if (_normal.Count > 30) _normal.Dequeue();
            return null;
        }
        // Three samples (about six seconds) filter single lost or delayed replies.
        if (++_high < 3 || !gaming || now - _lastAlert < _cooldown) return null;
        _lastAlert = now;
        var busiest = loads.Select(l => (l, Total: (l.DownloadMbps ?? 0) + (l.UploadMbps ?? 0)))
            .Where(x => x.Total >= 5).OrderByDescending(x => x.Total).FirstOrDefault();
        var cause = busiest.l is { } load
            ? $"Wahrscheinliche Ursache: „{load.Pc}“ überträgt gerade {busiest.Total:0} Mbit/s" + (load.TopProgram is { } program ? $" ({program})" : "") + "."
            : "Im LAN ist keine große Last zu sehen – Ursache vermutlich beim Anbieter, im WLAN oder beim Spielserver.";
        return $"Ping {ping:0} ms statt sonst etwa {baseline:0} ms. {cause}";
    }
}
