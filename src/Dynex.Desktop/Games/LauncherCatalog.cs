using Microsoft.Win32;
using System.IO;
using System.Xml;

namespace Dynex.Desktop.Games;

public sealed partial class GameDetector
{
    /// <summary>
    /// Ubisoft Connect, EA app and Battle.net register only installed games (their launchers are
    /// excluded by name and by the ignored-process list), so their install directories are positive
    /// classifications like Steam libraries.
    /// </summary>
    internal static IReadOnlyList<SteamGameInstall> ReadRegisteredInstalls(IEnumerable<(string Name, string Path, string Source)> entries)
    {
        var games = new Dictionary<string, SteamGameInstall>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path, source) in entries.Take(4096))
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) ||
                LauncherNames.Any(launcher => name.Contains(launcher, StringComparison.OrdinalIgnoreCase)) ||
                !TrySafeInstall(path, out var directory)) continue;
            games.TryAdd(directory, new SteamGameInstall(name.Trim(), directory, source));
        }
        return games.Values.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    private static readonly string[] LauncherNames = ["Battle.net", "EA app", "EA Desktop", "EA Core", "Origin", "Ubisoft Connect", "Uplay"];

    /// <summary>Xbox app / Game Pass PC installs: only executables listed in MicrosoftGame.config count.</summary>
    internal static IReadOnlyList<SteamGameInstall> ReadXboxCatalog(IEnumerable<string> xboxRoots)
    {
        var games = new List<SteamGameInstall>();
        foreach (var rawRoot in xboxRoots.Take(26))
        {
            if (!TryCanonicalDirectory(rawRoot, out var root)) continue;
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(root).Take(1024).ToArray(); }
            catch (Exception ex) when (IsFileAccessException(ex)) { continue; }
            foreach (var folder in folders)
            {
                var content = Path.Combine(folder, "Content");
                var config = ReadGameConfig(Path.Combine(content, "MicrosoftGame.config"));
                if (config is null || !TrySafeInstall(content, out var directory)) continue;
                var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var executable in config.Value.Executables)
                    if (TryGameExecutable(directory, executable, out var fullPath)) executables.Add(fullPath);
                if (executables.Count == 0) continue;
                var name = config.Value.Name ?? Path.GetFileName(folder);
                if (name.Length is 0 or > 128 || name.Any(char.IsControl)) continue;
                games.Add(new SteamGameInstall(name, directory, "Xbox", executables));
            }
        }
        return games.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    private static (string? Name, IReadOnlyList<string> Executables)? ReadGameConfig(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 1_048_576) return null;
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000, IgnoreComments = true
            });
            string? name = null;
            var executables = new List<string>();
            var inExecutableList = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "ExecutableList") inExecutableList = !reader.IsEmptyElement;
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "ExecutableList") inExecutableList = false;
                else if (reader.NodeType == XmlNodeType.Element && inExecutableList && reader.LocalName == "Executable" &&
                         reader.GetAttribute("Name") is { Length: > 0 and <= 1024 } executable && executables.Count < 64)
                    executables.Add(executable.Replace('/', '\\'));
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "ShellVisuals" &&
                         reader.GetAttribute("DefaultDisplayName") is { Length: > 0 and <= 128 } display && !display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    name = display;
            }
            return executables.Count == 0 ? null : (name, executables);
        }
        catch (Exception ex) when (IsFileAccessException(ex) || ex is XmlException) { return null; }
    }

    internal static IReadOnlyList<SteamGameInstall> FindOtherLauncherGames() =>
        ReadRegisteredInstalls(FindRegisteredLauncherInstalls()).Concat(ReadXboxCatalog(FindXboxRoots())).ToArray();

    private static IEnumerable<(string Name, string Path, string Source)> FindRegisteredLauncherInstalls()
    {
        var entries = new List<(string, string, string)>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using (var ubisoft = machine.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs"))
                    foreach (var id in ubisoft?.GetSubKeyNames().Take(1024) ?? [])
                    {
                        if (id.Length is 0 or > 16 || !id.All(char.IsAsciiDigit)) continue;
                        using var game = ubisoft!.OpenSubKey(id);
                        if (game?.GetValue("InstallDir") is not string dir) continue;
                        using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install " + id);
                        var name = uninstall?.GetValue("DisplayName") as string ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
                        entries.Add((name, dir, "Ubisoft"));
                    }
                foreach (var vendor in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts" })
                {
                    using var ea = machine.OpenSubKey(vendor);
                    foreach (var title in ea?.GetSubKeyNames().Take(1024) ?? [])
                    {
                        using var game = ea!.OpenSubKey(title);
                        if (game?.GetValue("Install Dir") is string dir) entries.Add((title, dir, "EA"));
                    }
                }
                using var programs = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var keyName in programs?.GetSubKeyNames().Take(8192) ?? [])
                {
                    using var entry = programs!.OpenSubKey(keyName);
                    if (entry?.GetValue("Publisher") is "Blizzard Entertainment" &&
                        entry.GetValue("DisplayName") is string name && entry.GetValue("InstallLocation") is string dir)
                        entries.Add((name, dir, "Battle.net"));
                }
            }
            catch (Exception ex) when (IsFileAccessException(ex)) { }
        }
        return entries;
    }

    private static IReadOnlyList<string> FindXboxRoots()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => Path.Combine(d.RootDirectory.FullName, "XboxGames")).Where(Directory.Exists).ToArray();
        }
        catch (Exception ex) when (IsFileAccessException(ex)) { return []; }
    }
}
