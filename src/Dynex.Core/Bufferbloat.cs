namespace Dynex.Core;

public sealed record BufferbloatRating(string Grade, double IncreaseMs, string Summary, string? Recommendation);

/// <summary>Rates the latency increase under full download load (similar to common bufferbloat grades).</summary>
public static class Bufferbloat
{
    public static BufferbloatRating? Rate(double? idleMs, double? loadedMs)
    {
        if (idleMs is not { } idle || loadedMs is not { } loaded || !double.IsFinite(idle) || !double.IsFinite(loaded) || idle < 0 || loaded < 0)
            return null;
        var increase = Math.Max(0, loaded - idle);
        var grade = increase switch { < 5 => "A+", < 30 => "A", < 60 => "B", < 200 => "C", < 400 => "D", _ => "F" };
        var summary = $"Ping im Leerlauf {idle:0} ms, unter Volllast {loaded:0} ms (+{increase:0} ms) · Bewertung {grade}";
        string? recommendation = grade switch
        {
            "A+" or "A" => null,
            "B" => "Leichter Anstieg unter Last. DYNEX-Begrenzung beim Spielen reicht meist aus.",
            _ => "Starker Anstieg unter Last (Bufferbloat). Im Router „Smart Queue“/SQM (fq_codel oder CAKE) bzw. QoS einschalten. " +
                 "DYNEX bremst nur Downloads der DYNEX-PCs, nicht Konsolen, Handys oder Uploads."
        };
        return new(grade, increase, summary, recommendation);
    }

    /// <summary>Median of the samples that answered; null when fewer than two did.</summary>
    public static double? Median(IEnumerable<double?> samples)
    {
        var values = samples.Where(v => v is { } d && double.IsFinite(d) && d >= 0).Select(v => v!.Value).Order().ToArray();
        if (values.Length < 2) return null;
        var mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
