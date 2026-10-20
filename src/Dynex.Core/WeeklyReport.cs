namespace Dynex.Core;

public static class WeeklyReport
{
    public static string Build(IEnumerable<SessionSummary> sessions, IEnumerable<QualityEvent> quality, DateTimeOffset now)
    {
        var since = now.AddDays(-7);
        var week = sessions.Where(s => s.StartedAtUtc >= since).ToArray();
        var events = quality.Where(e => e.StartUtc >= since).ToArray();
        if (week.Length == 0 && events.Length == 0) return "In den letzten 7 Tagen wurden noch keine Spielsitzungen oder Störungen erfasst.";
        var played = TimeSpan.FromTicks(week.Sum(s => s.Duration.Ticks));
        var pings = week.Where(s => s.AveragePingMs is not null).ToArray();
        var lines = new List<string>
        {
            $"Gespielt: {played.TotalHours:0.#} h in {week.Length} {(week.Length == 1 ? "Sitzung" : "Sitzungen")}" +
                (week.Where(s => !string.IsNullOrEmpty(s.Game)).GroupBy(s => s.Game).OrderByDescending(g => g.Sum(s => s.Duration.Ticks)).FirstOrDefault() is { } top
                    ? $" · meistgespielt: {top.Key}" : ""),
            pings.Length > 0
                ? $"Ping: Ø {pings.Average(s => s.AveragePingMs!.Value):0} ms · höchster Wert {pings.Max(s => s.MaxPingMs ?? 0):0} ms"
                : "Ping: keine Messwerte",
            $"Internetstörungen: {events.Count(e => e.Kind == QualityEvent.Outage)} Ausfälle · {events.Count(e => e.Kind == QualityEvent.Spike)} Pingspitzen"
        };
        return string.Join(Environment.NewLine, lines);
    }
}
