namespace Dynex.Desktop;

/// <summary>A shared budget for all processes with this executable name. Null means unlimited in that direction.</summary>
public sealed record ApplicationRule(string ProcessName, double? DownloadMbps = null, double? UploadMbps = null, bool Enabled = true);

public static class ApplicationRules
{
    public const int MaximumCount = 32;
    public static string NormalizeName(string? value)
    {
        var name = (value ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length is < 1 or > 64 || name is "." or ".." || name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c)))
            throw new ArgumentException("Programmname ohne Pfad eingeben, zum Beispiel Steam.exe.");
        return name.ToLowerInvariant();
    }
    public static ApplicationRule[] Normalize(IEnumerable<ApplicationRule>? rules)
    {
        var result = (rules ?? []).Take(MaximumCount + 1).Select(r => r is null
            ? throw new ArgumentException("Ungültige Programmregel.") : r with { ProcessName = NormalizeName(r.ProcessName) }).ToArray();
        Validate(result);
        return result.Length == 0 ? Array.Empty<ApplicationRule>() : result;
    }
    public static void Validate(IEnumerable<ApplicationRule>? rules)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules ?? [])
        {
            if (rule is null) throw new ArgumentException("Ungültige Programmregel.");
            if (!names.Add(NormalizeName(rule.ProcessName))) throw new ArgumentException("Für jedes Programm ist nur eine Regel erlaubt.");
            if (names.Count > MaximumCount) throw new ArgumentException($"Höchstens {MaximumCount} Programmregeln sind möglich.");
            foreach (var rate in new[] { rule.DownloadMbps, rule.UploadMbps })
                if (rate.HasValue && (!double.IsFinite(rate.Value) || rate < .1 || rate > 100_000))
                    throw new ArgumentException("Programmlimits müssen zwischen 0,1 und 100000 Mbit/s liegen.");
            if (rule.DownloadMbps is null && rule.UploadMbps is null) throw new ArgumentException("Mindestens ein Download- oder Uploadlimit eingeben.");
        }
    }
}
