using System.IO;
using System.Text;

namespace Dynex.Desktop;

/// <summary>Local crash reports only. Nothing is uploaded; the user can attach them via the diagnostics export.</summary>
internal static class CrashLog
{
    private const int Keep = 10;
    internal static string DirectoryPath => Path.Combine(new SettingsStore().DirectoryPath, "crashes");
    private static string SeenMarker => Path.Combine(DirectoryPath, "seen.txt");

    internal static void Write(Exception error, string source)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.txt");
            var text = new StringBuilder()
                .AppendLine($"DYNEX {DynexRuntime.AppVersion} · {DateTimeOffset.UtcNow:O} · {source}")
                .AppendLine(Environment.OSVersion.ToString())
                .AppendLine(error.ToString());
            File.WriteAllText(path, text.ToString());
            foreach (var old in Reports().Skip(Keep)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Never fail while reporting a failure. */ }
    }

    /// <summary>Newest first.</summary>
    internal static IReadOnlyList<string> Reports()
    {
        try
        {
            return Directory.Exists(DirectoryPath)
                ? Directory.GetFiles(DirectoryPath, "crash-*.txt").OrderByDescending(p => p, StringComparer.Ordinal).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Returns the newest report once, then remembers it as seen.</summary>
    internal static string? TakeUnseen()
    {
        try
        {
            var newest = Reports().FirstOrDefault();
            if (newest is null) return null;
            var name = Path.GetFileName(newest);
            if (File.Exists(SeenMarker) && File.ReadAllText(SeenMarker).Trim() == name) return null;
            File.WriteAllText(SeenMarker, name);
            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
