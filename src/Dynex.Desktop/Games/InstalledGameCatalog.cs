using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace Dynex.Desktop.Games;

public sealed partial class GameDetector
{
    // These are positive local product classifications, never a scan of arbitrary EXEs or drives.
    internal static IReadOnlyList<SteamGameInstall> ReadEpicCatalog(IEnumerable<string> manifestRoots)
    {
        var games = new List<SteamGameInstall>();
        foreach (var rawRoot in manifestRoots.Take(16))
        {
            if (!TryCanonicalDirectory(rawRoot, out var root)) continue;
            foreach (var path in ManifestFiles(root, "*.item"))
            {
                using var document = ReadManifestJson(path);
                if (document is null) continue;
                var item = document.RootElement;
                if (!JsonText(item, "AppName", 256, out var appName) ||
                    !JsonText(item, "DisplayName", 128, out var name) ||
                    !JsonText(item, "InstallLocation", 1024, out var install) ||
                    !JsonText(item, "LaunchExecutable", 1024, out var executable) ||
                    !JsonTrue(item, "bIsApplication") || !JsonTrue(item, "bIsExecutable") ||
                    !JsonOptionalFalse(item, "bIsIncompleteInstall") ||
                    !item.TryGetProperty("AppCategories", out var categories) || categories.ValueKind != JsonValueKind.Array ||
                    categories.GetArrayLength() is 0 or > 32 || !categories.EnumerateArray().Any(category =>
                        category.ValueKind == JsonValueKind.String && category.GetString()!.Equals("games", StringComparison.OrdinalIgnoreCase))) continue;

                // DLC and engine/plugin packages must not lend their install directory a game identity.
                if (item.TryGetProperty("MainGameAppName", out var parent) &&
                    (parent.ValueKind != JsonValueKind.String ||
                     (!string.IsNullOrEmpty(parent.GetString()) && !parent.GetString()!.Equals(appName, StringComparison.OrdinalIgnoreCase)))) continue;
                if (!TrySafeInstall(install, out var directory) || !TryGameExecutable(directory, executable, out _)) continue;
                games.Add(new SteamGameInstall(name, directory, "Epic"));
            }
        }
        return games.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    internal static IReadOnlyList<SteamGameInstall> ReadGogCatalog(IEnumerable<string> installRoots)
    {
        var games = new List<SteamGameInstall>();
        foreach (var rawRoot in installRoots.Distinct(StringComparer.OrdinalIgnoreCase).Take(4096))
        {
            if (!TrySafeInstall(rawRoot, out var root)) continue;
            foreach (var path in ManifestFiles(root, "goggame-*.info"))
            {
                using var document = ReadManifestJson(path);
                if (document is null) continue;
                var item = document.RootElement;
                if (!JsonText(item, "gameId", 32, out var id) || !id.All(char.IsAsciiDigit) ||
                    !Path.GetFileName(path).Equals($"goggame-{id}.info", StringComparison.OrdinalIgnoreCase) ||
                    !JsonText(item, "name", 128, out var name)) continue;
                if (item.TryGetProperty("rootGameId", out var parent) &&
                    (parent.ValueKind != JsonValueKind.String || (!string.IsNullOrEmpty(parent.GetString()) && parent.GetString() != id))) continue;
                if (!item.TryGetProperty("playTasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array ||
                    tasks.GetArrayLength() is 0 or > 128) continue;

                var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var task in tasks.EnumerateArray())
                {
                    if (task.ValueKind != JsonValueKind.Object || HasDuplicateProperties(task) ||
                        !JsonText(task, "category", 32, out var category) || !category.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                        !JsonText(task, "type", 32, out var type) || !type.Equals("FileTask", StringComparison.OrdinalIgnoreCase) ||
                        !JsonText(task, "path", 1024, out var executable) || !TryGameExecutable(root, executable, out var fullPath)) continue;
                    executables.Add(fullPath);
                }
                // GOG lists actual game tasks separately from launcher, editor, configuration and URL tasks.
                if (executables.Count > 0) games.Add(new SteamGameInstall(name, root, "GOG", executables));
            }
        }
        return games.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    internal static IReadOnlyList<string> FindEpicManifestRoots()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return string.IsNullOrWhiteSpace(programData) ? [] : [Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests")];
    }

    internal static IReadOnlyList<string> FindGogInstallRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var games = registry.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                foreach (var id in games?.GetSubKeyNames().Take(4096) ?? [])
                {
                    if (id.Length is 0 or > 32 || !id.All(char.IsAsciiDigit)) continue;
                    using var game = games!.OpenSubKey(id);
                    if (game?.GetValue("path") is string path && TrySafeInstall(path, out var install)) roots.Add(install);
                }
                // Offline GOG installers may register only the standard uninstaller entry.
                using var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var keyName in uninstall?.GetSubKeyNames().Take(8192) ?? [])
                {
                    var separator = keyName.IndexOf('_');
                    if (separator <= 0 || separator > 32 || !keyName[..separator].All(char.IsAsciiDigit) ||
                        !keyName[separator..].Equals("_is1", StringComparison.OrdinalIgnoreCase)) continue;
                    using var entry = uninstall!.OpenSubKey(keyName);
                    if (entry?.GetValue("Publisher") is string publisher && publisher.Equals("GOG.com", StringComparison.OrdinalIgnoreCase) &&
                        entry.GetValue("InstallLocation") is string path && TrySafeInstall(path, out var install)) roots.Add(install);
                }
            }
            catch (Exception ex) when (IsFileAccessException(ex)) { }
        }
        return roots.ToArray();
    }

    private static IEnumerable<string> ManifestFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Take(4096).ToArray(); }
        catch (Exception ex) when (IsFileAccessException(ex)) { return []; }
    }

    private static JsonDocument? ReadManifestJson(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 1_048_576) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;
            var json = bytes.AsMemory();
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind == JsonValueKind.Object && !HasDuplicateProperties(document.RootElement)) return document;
            document.Dispose();
            return null;
        }
        catch (Exception ex) when (IsFileAccessException(ex) || ex is JsonException) { return null; }
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return value.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    private static bool JsonText(JsonElement item, string key, int maximum, out string text)
    {
        text = "";
        if (!item.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString()!;
        return text.Length > 0 && text.Length <= maximum && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
    }

    private static bool JsonTrue(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool JsonOptionalFalse(JsonElement item, string key) =>
        !item.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.False;

    private static bool TrySafeInstall(string path, out string directory) =>
        TryCanonicalDirectory(path, out directory) && !directory.Equals(Path.GetPathRoot(directory), StringComparison.OrdinalIgnoreCase);

    private static bool TryGameExecutable(string directory, string relative, out string fullPath)
    {
        fullPath = "";
        if (Path.IsPathRooted(relative) || HasUnsafePathSegment(relative) || relative.IndexOf(':') >= 0 ||
            !relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(directory, relative));
            return IsPathWithin(fullPath, directory) && File.Exists(fullPath) &&
                !IgnoredProcesses.Contains(NormalizeProcessName(fullPath)) && !IsSupportingExecutable(fullPath, directory);
        }
        catch (Exception ex) when (IsFileAccessException(ex)) { return false; }
    }
}
