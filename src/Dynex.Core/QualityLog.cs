namespace Dynex.Core;

public sealed record QualityEvent(DateTimeOffset StartUtc, TimeSpan Duration, string Kind, double? PeakMs)
{
    public const string Outage = "Ausfall";
    public const string Spike = "Pingspitze";
    public string Csv => $"{StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss};{Duration.TotalSeconds:0};{Kind};{(PeakMs is { } p ? p.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "")}";
}

/// <summary>
/// Records internet problems as evidence for the provider: an outage is several lost internet
/// pings while the router still answers (so the LAN is fine); a spike is a clearly raised internet ping.
/// </summary>
public sealed class QualityMonitor
{
    private readonly Queue<double> _normal = new();
    private DateTimeOffset? _start;
    private string? _kind;
    private int _count;
    private double? _peak;
    private DateTimeOffset _last;

    /// <summary>Returns an event once it has ended.</summary>
    public QualityEvent? Observe(double? internetMs, double? routerMs, DateTimeOffset now)
    {
        var baseline = _normal.Count >= 5 ? _normal.Order().ElementAt(_normal.Count / 2) : (double?)null;
        string? kind = internetMs is null && routerMs is not null ? QualityEvent.Outage
            : internetMs is { } ms && baseline is { } normal && ms > Math.Max(normal + 100, normal * 3) ? QualityEvent.Spike : null;
        if (kind is null && internetMs is { } ok) { _normal.Enqueue(ok); if (_normal.Count > 60) _normal.Dequeue(); }

        QualityEvent? ended = null;
        if (_start is { } start && kind != _kind)
        {
            // At least three samples (about six seconds); shorter blips are noise.
            if (_count >= 3) ended = new(start, _last - start, _kind!, _peak);
            _start = null; _kind = null; _count = 0; _peak = null;
        }
        if (kind is not null)
        {
            if (_start is null) { _start = now; _kind = kind; }
            _count++;
            _last = now;
            if (internetMs is { } peak) _peak = Math.Max(_peak ?? 0, peak);
        }
        return ended;
    }
}
