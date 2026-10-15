namespace Dynex.Core;

public sealed record SessionSummary(DateTimeOffset StartedAtUtc, TimeSpan Duration, string? Game,
    double? AveragePingMs, double? MaxPingMs, double? PingLossPercent, TimeSpan LimitedFor)
{
    public string Text =>
        $"{(string.IsNullOrEmpty(Game) ? "Spielsitzung" : Game)}: {Duration.TotalMinutes:0} min · " +
        (AveragePingMs is { } avg ? $"Ø Ping {avg:0} ms (max {MaxPingMs:0} ms)" : "kein Ping gemessen") +
        (PingLossPercent is > 0 ? $" · {PingLossPercent:0} % ohne Antwort" : "") +
        $" · Downloads {LimitedFor.TotalMinutes:0} min begrenzt";
}

/// <summary>Collects one summary per local gaming session from the regular 2-second samples.</summary>
public sealed class SessionTracker(TimeSpan? minimumDuration = null)
{
    private readonly TimeSpan _minimum = minimumDuration ?? TimeSpan.FromMinutes(2);
    private DateTimeOffset? _start, _last;
    private string? _game;
    private readonly List<double> _pings = [];
    private int _samples, _lost;
    private TimeSpan _limited;

    /// <summary>Returns a summary when a session that lasted long enough has just ended.</summary>
    public SessionSummary? Observe(bool gaming, string? game, double? pingMs, bool limitActive, DateTimeOffset now)
    {
        if (gaming)
        {
            if (_start is null) { _start = now; _pings.Clear(); _samples = _lost = 0; _limited = TimeSpan.Zero; }
            else if (limitActive && _last is { } last) _limited += now - last;
            _game ??= string.IsNullOrWhiteSpace(game) ? null : game;
            _samples++;
            if (pingMs is { } ping && double.IsFinite(ping) && ping >= 0) _pings.Add(ping); else _lost++;
            _last = now;
            return null;
        }
        if (_start is not { } start) return null;
        var duration = (_last ?? now) - start;
        var summary = duration < _minimum ? null : new SessionSummary(start, duration, _game,
            _pings.Count > 0 ? _pings.Average() : null, _pings.Count > 0 ? _pings.Max() : null,
            _samples > 0 ? 100.0 * _lost / _samples : null, _limited);
        _start = _last = null; _game = null;
        return summary;
    }
}
