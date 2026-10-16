namespace Dynex.Desktop;

internal sealed class UsageRateAccumulator
{
    private readonly Dictionary<string, Baseline> baselines = new(StringComparer.Ordinal);
    internal static bool IsFreshSample(DateTimeOffset sourceUtc, DateTimeOffset now)
    {
        var age = now - sourceUtc;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(5);
    }

    internal UsageInterval? Observe(string id, string name, double? downMbps, double? upMbps,
        DateTimeOffset sourceUtc, DateTimeOffset atUtc, bool deduplicateSource = false)
    {
        baselines.TryGetValue(id, out var previous);
        if (deduplicateSource && previous is not null && sourceUtc <= previous.SourceUtc &&
            downMbps == previous.DownMbps && upMbps == previous.UpMbps) return null;
        UsageInterval? interval = null;
        if (previous is not null)
        {
            var elapsed = atUtc - previous.AtUtc;
            if (elapsed > TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(6))
                interval = new(id, name, previous.AtUtc, atUtc,
                    Estimate(previous.DownMbps, downMbps, elapsed.TotalSeconds),
                    Estimate(previous.UpMbps, upMbps, elapsed.TotalSeconds));
        }
        baselines[id] = new(atUtc, downMbps, upMbps, sourceUtc);
        return interval;
    }

    internal void Clear() => baselines.Clear();
    private static double? Estimate(double? prior, double? current, double seconds) =>
        prior is { } a && current is { } b && double.IsFinite(a) && double.IsFinite(b)
            ? Math.Max(0, (a + b) / 2 * seconds / 8000) : null;
    private sealed record Baseline(DateTimeOffset AtUtc, double? DownMbps, double? UpMbps, DateTimeOffset SourceUtc);
}
