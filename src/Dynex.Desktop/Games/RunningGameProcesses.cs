using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dynex.Desktop.Games;

public sealed partial class GameDetector
{
    private static readonly Regex SelectableProcessName = new("^[a-zA-Z0-9][a-zA-Z0-9_. -]{0,79}$", RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> GetRunningProcessNames()
    {
        int currentSession;
        try { using var current = Process.GetCurrentProcess(); currentSession = current.SessionId; }
        catch (Exception ex) when (IsProcessAccessException(ex)) { return []; }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != currentSession) continue;
                    names.Add(process.ProcessName);
                }
                catch (Exception ex) when (IsProcessAccessException(ex)) { }
            }
        }

        return FilterSelectableProcessNames(names);
    }

    internal static IReadOnlyList<string> FilterSelectableProcessNames(IEnumerable<string> processNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawName in processNames)
        {
            if (string.IsNullOrWhiteSpace(rawName) || rawName.IndexOfAny(['\\', '/']) >= 0) continue;
            var name = NormalizeProcessName(rawName.Trim());
            if (!SelectableProcessName.IsMatch(name) || IgnoredProcesses.Contains(name) ||
                name.Equals("DYNEX", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Dynex.Shaper", StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(name);
        }
        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string AppendConfiguredProcess(string existing, string processName)
    {
        var valid = FilterSelectableProcessNames([processName]);
        if (valid.Count != 1) throw new ArgumentException("Ungültiger Prozessname.", nameof(processName));
        var name = valid[0];
        if (ParseAdditionalNames(existing).Contains(name)) return existing;
        var result = string.IsNullOrEmpty(existing) ? name : existing + "; " + name;
        if (result.Length > 4096) throw new ArgumentException("Die Prozessliste darf höchstens 4096 Zeichen lang sein.", nameof(existing));
        return result;
    }
}
