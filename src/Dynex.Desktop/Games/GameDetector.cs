using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Dynex.Desktop.Games;

public sealed record GameDetectionResult(
    string? GameName,
    string? ProcessName,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAt,
    bool IsRunning,
    string Source)
{
    public static GameDetectionResult None { get; } = new(null, null, null, null, false, "None");
}

/// <summary>Read-only detection of known/configured game processes and positive Steam, Epic and GOG installs.</summary>
public sealed partial class GameDetector
{
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromSeconds(60);
    private static readonly GameDetector Shared = new(FindSteamRoots, EnumerateProcesses, FindEpicManifestRoots, FindGogInstallRoots);
    private static readonly IReadOnlyDictionary<string, string> BuiltInGames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["VALORANT-Win64-Shipping"] = "VALORANT",
            ["Marvel-Win64-Shipping"] = "Marvel Rivals",
            ["cs2"] = "Counter-Strike 2",
            ["FortniteClient-Win64-Shipping"] = "Fortnite",
            ["r5apex"] = "Apex Legends",
            ["dota2"] = "Dota 2",
            ["League of Legends"] = "League of Legends",
            ["RocketLeague"] = "Rocket League",
            ["TslGame"] = "PUBG: BATTLEGROUNDS",
            ["Overwatch"] = "Overwatch",
            ["Wow"] = "World of Warcraft",
            ["WowClassic"] = "World of Warcraft Classic",
            ["WowClassicT"] = "World of Warcraft Classic",
            ["Diablo IV"] = "Diablo IV",
            ["Diablo III"] = "Diablo III",
            ["SC2_x64"] = "StarCraft II",
            ["HeroesOfTheStorm_x64"] = "Heroes of the Storm",
            ["Minecraft.Windows"] = "Minecraft",
            ["RobloxPlayerBeta"] = "Roblox",
            ["EscapeFromTarkov"] = "Escape from Tarkov",
            ["Warframe.x64"] = "Warframe",
            ["destiny2"] = "Destiny 2",
            ["RainbowSix"] = "Rainbow Six Siege",
            ["RainbowSix_Vulkan"] = "Rainbow Six Siege",
            ["RainbowSix_DX11"] = "Rainbow Six Siege",
            ["GTA5"] = "Grand Theft Auto V",
            ["GTA5_Enhanced"] = "Grand Theft Auto V Enhanced"
        };

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamservice", "steamwebhelper", "steamerrorreporter", "steamerrorreporter64",
        "steamoverlayui", "gameoverlayui", "crashhandler", "crashhandler64", "crashpad_handler",
        "minidump", "unrealcefsubprocess", "crashreportclient", "crashreportclienteditor",
        "wallpaper32", "wallpaper64", "webwallpaper32", "wallpaperui",
        "EpicGamesLauncher", "EpicWebHelper", "GalaxyClient", "GalaxyClientService",
        "GalaxyCommunication", "Battle.net", "Agent", "RiotClientServices", "RiotClientUx",
        "RiotClientUxRender", "EADesktop", "EALauncher", "EABackgroundService", "UbisoftConnect",
        "upc", "UplayWebCore", "Launcher", "LauncherPatcher", "UnityCrashHandler32",
        "UnityCrashHandler64", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64",
        "vc_redist.x64", "vc_redist.x86", "DXSETUP", "UnrealEditor", "UnrealEditor-Cmd"
    };

    private readonly Func<IReadOnlyList<string>> _steamRoots;
    private readonly Func<IEnumerable<ProcessSnapshot>> _processes;
    private readonly Func<IReadOnlyList<string>> _epicManifestRoots;
    private readonly Func<IReadOnlyList<string>> _gogInstallRoots;
    private readonly object _catalogGate = new();
    private IReadOnlyList<SteamGameInstall> _catalog = [];
    private long _catalogUpdatedAt;

    internal GameDetector(Func<IReadOnlyList<string>> steamRoots, Func<IEnumerable<ProcessSnapshot>> processes,
        Func<IReadOnlyList<string>>? epicManifestRoots = null, Func<IReadOnlyList<string>>? gogInstallRoots = null)
    {
        _steamRoots = steamRoots;
        _processes = processes;
        _epicManifestRoots = epicManifestRoots ?? (() => []);
        _gogInstallRoots = gogInstallRoots ?? (() => []);
    }

    /// <summary>Scans the current process list; additional names are executable/process basenames.</summary>
    public static GameDetectionResult Scan(string additionalNames) => Shared.ScanCore(additionalNames);

    /// <summary>All recognized game processes, so another running game is never throttled.</summary>
    public static IReadOnlyList<GameDetectionResult> ScanAll(string additionalNames) =>
        DetectAll(Shared._processes(), Shared.GetCatalog(), ParseAdditionalNames(additionalNames));

    internal GameDetectionResult ScanCore(string additionalNames)
    {
        var configured = ParseAdditionalNames(additionalNames);
        var catalog = GetCatalog();
        return Detect(_processes(), catalog, configured);
    }

    internal IReadOnlyList<SteamGameInstall> GetCatalog()
    {
        lock (_catalogGate)
        {
            var now = Stopwatch.GetTimestamp();
            if (_catalogUpdatedAt == 0 || Stopwatch.GetElapsedTime(_catalogUpdatedAt, now) >= CatalogLifetime)
            {
                _catalog = ReadSteamCatalog(_steamRoots()).Concat(ReadEpicCatalog(_epicManifestRoots()))
                    .Concat(ReadGogCatalog(_gogInstallRoots()))
                    .OrderByDescending(game => game.InstallPath.Length).ToArray();
                _catalogUpdatedAt = now;
            }
            return _catalog;
        }
    }

    internal static GameDetectionResult Detect(
        IEnumerable<ProcessSnapshot> processes,
        IReadOnlyList<SteamGameInstall> steamGames,
        IReadOnlySet<string> configuredNames) => DetectAll(processes, steamGames, configuredNames).FirstOrDefault() ?? GameDetectionResult.None;

    internal static IReadOnlyList<GameDetectionResult> DetectAll(
        IEnumerable<ProcessSnapshot> processes,
        IReadOnlyList<SteamGameInstall> steamGames,
        IReadOnlySet<string> configuredNames)
    {
        var matches = new List<(int Priority, ProcessSnapshot Process, string Game, string Source)>();
        foreach (var process in processes)
        {
            var name = NormalizeProcessName(process.ProcessName);
            if (name.Length == 0 || IgnoredProcesses.Contains(name)) continue;

            if (BuiltInGames.TryGetValue(name, out var knownTitle) && knownTitle.Length > 0)
            {
                matches.Add((0, process, knownTitle, "BuiltIn"));
                continue;
            }

            if (configuredNames.Contains(name))
            {
                matches.Add((1, process, name, "Configured"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(process.ExecutablePath)) continue;
            foreach (var game in steamGames)
            {
                if (!IsPathWithin(process.ExecutablePath, game.InstallPath)) continue;
                if (IsSupportingExecutable(process.ExecutablePath, game.InstallPath)) continue;
                if (game.GameExecutables is not null && !game.GameExecutables.Contains(Path.GetFullPath(process.ExecutablePath))) continue;
                matches.Add((2, process, game.Name, game.Source));
                break;
            }
        }

        return matches.OrderBy(m => m.Priority).ThenBy(m => m.Process.ProcessId)
            .Select(m => new GameDetectionResult(m.Game, NormalizeProcessName(m.Process.ProcessName), m.Process.ProcessId,
                m.Process.StartedAt, true, m.Source)).ToArray();
    }

    internal static IReadOnlySet<string> ParseAdditionalNames(string? value)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(value) || value.Length > 4096) return names;
        foreach (var entry in value.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.IndexOfAny(['\\', '/']) >= 0) continue;
            var name = NormalizeProcessName(entry);
            if (name.Length is > 0 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ' ' or '.'))
                names.Add(name);
        }
        return names;
    }

    internal static IReadOnlyList<SteamGameInstall> ReadSteamCatalog(IEnumerable<string> steamRoots)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appTypes = new Dictionary<uint, string>();
        foreach (var rawRoot in steamRoots.Take(16))
        {
            if (!TryCanonicalDirectory(rawRoot, out var root)) continue;
            foreach (var (id, type) in SteamAppInfoReader.ReadTypes(Path.Combine(root, "appcache", "appinfo.vdf")))
            {
                if (!appTypes.ContainsKey(id) || !IsSteamGameType(type)) appTypes[id] = type;
            }
            AddLibrary(libraries, root);
            var vdfPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!TryReadVdf(vdfPath, out var document)) continue;
            if (!TryGetChild(document, "libraryfolders", out var folderObject)) folderObject = document;

            foreach (var item in folderObject.Take(64))
            {
                if (!int.TryParse(item.Key, out _)) continue;
                var libraryPath = item.Value.Scalar;
                if (libraryPath is null && item.Value.Children is { } nested && nested.TryGetValue("path", out var path))
                    libraryPath = path.Scalar;
                if (libraryPath is not null && TryCanonicalDirectory(libraryPath, out var canonical))
                    AddLibrary(libraries, canonical);
            }
        }

        var games = new List<SteamGameInstall>();
        foreach (var library in libraries)
        {
            var appsPath = Path.Combine(library, "steamapps");
            if (!Directory.Exists(appsPath)) continue;
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(appsPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).Take(4096).ToArray(); }
            catch (Exception ex) when (IsFileAccessException(ex)) { continue; }

            foreach (var manifestPath in manifests)
            {
                if (!TryReadVdf(manifestPath, out var manifest)) continue;
                if (!TryGetChild(manifest, "AppState", out var appState)) appState = manifest;
                if (!TryGetScalar(appState, "appid", out var appIdText) || !uint.TryParse(appIdText, out var appId) ||
                    appId == 0 || appId == 431960 || !appTypes.TryGetValue(appId, out var appType) ||
                    !IsSteamGameType(appType)) continue;
                var manifestName = Path.GetFileNameWithoutExtension(manifestPath);
                if (!uint.TryParse(manifestName.AsSpan("appmanifest_".Length), out var fileAppId) || fileAppId != appId) continue;
                if (!TryGetScalar(appState, "name", out var name) || !TryGetScalar(appState, "installdir", out var installDir)) continue;
                if (name.Length is 0 or > 128 || name.Any(char.IsControl) || installDir.Length is 0 or > 240 || HasUnsafePathSegment(installDir)) continue;
                if (Path.IsPathRooted(installDir) || installDir.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

                var commonPath = Path.Combine(appsPath, "common");
                var installPath = Path.GetFullPath(Path.Combine(commonPath, installDir));
                if (!IsPathWithin(installPath, commonPath) || !Directory.Exists(installPath)) continue;
                games.Add(new SteamGameInstall(name, installPath));
            }
        }
        return games.OrderByDescending(game => game.InstallPath.Length).ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddLibrary(HashSet<string> libraries, string path)
    {
        var appsPath = Path.Combine(path, "steamapps");
        if (Directory.Exists(appsPath)) libraries.Add(path);
    }

    internal static IReadOnlyList<string> FindSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                AddRegistrySteamPath(roots, machine.OpenSubKey(@"SOFTWARE\Valve\Steam"));
                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                AddRegistrySteamPath(roots, user.OpenSubKey(@"Software\Valve\Steam"));
            }
            catch (Exception ex) when (IsFileAccessException(ex) || ex is System.Security.SecurityException) { }
        }

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var defaultPath = Path.Combine(fallback, "Steam");
            if (Directory.Exists(defaultPath)) roots.Add(defaultPath);
        }
        return roots.ToArray();
    }

    private static void AddRegistrySteamPath(HashSet<string> roots, RegistryKey? key)
    {
        using (key)
        {
            if (key is null) return;
            foreach (var valueName in new[] { "SteamPath", "InstallPath" })
            {
                if (key.GetValue(valueName) is string path && TryCanonicalDirectory(path, out var canonical))
                    roots.Add(canonical);
            }
        }
    }

    private static IEnumerable<ProcessSnapshot> EnumerateProcesses()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { yield break; }

        foreach (var process in processes)
        {
            using (process)
            {
                string? name = null;
                try { name = process.ProcessName; }
                catch (Exception ex) when (IsProcessAccessException(ex)) { }
                if (string.IsNullOrWhiteSpace(name)) continue;

                string? executablePath = null;
                try { executablePath = TryGetImagePath(process.Id); }
                catch (Exception ex) when (IsProcessAccessException(ex)) { }

                DateTimeOffset? startedAt = null;
                try { startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); }
                catch (Exception ex) when (IsProcessAccessException(ex)) { }
                yield return new ProcessSnapshot(name, process.Id, executablePath, startedAt);
            }
        }
    }

    private static string? TryGetImagePath(int processId)
    {
        const uint QueryLimitedInformation = 0x1000;
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private static bool TryReadVdf(string path, out Dictionary<string, VdfValue> result)
    {
        result = new Dictionary<string, VdfValue>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 1_048_576) return false;
            var text = File.ReadAllText(path);
            if (!VdfParser.TryParse(text, out result)) return false;
            return true;
        }
        catch (Exception ex) when (IsFileAccessException(ex)) { return false; }
    }

    private static bool TryGetChild(Dictionary<string, VdfValue> value, string key, out Dictionary<string, VdfValue> child)
    {
        if (value.TryGetValue(key, out var entry) && entry.Children is not null)
        { child = entry.Children; return true; }
        child = value;
        return false;
    }

    private static bool TryGetScalar(Dictionary<string, VdfValue> value, string key, out string scalar)
    {
        if (value.TryGetValue(key, out var entry) && entry.Scalar is not null)
        { scalar = entry.Scalar; return true; }
        scalar = "";
        return false;
    }

    private static bool TryCanonicalDirectory(string path, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || HasTraversalSegment(path) || !Path.IsPathRooted(path)) return false;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Directory.Exists(canonical);
        }
        catch (Exception ex) when (IsFileAccessException(ex)) { return false; }
    }

    private static bool IsPathWithin(string candidate, string directory)
    {
        try
        {
            var fullCandidate = Path.GetFullPath(candidate);
            var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return fullCandidate.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   fullCandidate.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsFileAccessException(ex)) { return false; }
    }

    private static bool HasTraversalSegment(string value) => value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "..");
    private static bool HasUnsafePathSegment(string value) => value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");
    private static string NormalizeProcessName(string name)
    {
        var basename = Path.GetFileName(name.Trim());
        return basename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? basename[..^4] : basename;
    }

    private static bool IsSteamGameType(string type) => type.Equals("Game", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("Demo", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportingExecutable(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).SkipLast(1).Any(segment =>
            segment.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("redist", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("redistributables", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("DirectX", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("BattlEye", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".egstore", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFileAccessException(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static bool IsProcessAccessException(Exception ex) => ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder imageName, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    internal sealed record ProcessSnapshot(string ProcessName, int ProcessId, string? ExecutablePath, DateTimeOffset? StartedAt);
    internal sealed record SteamGameInstall(string Name, string InstallPath, string Source = "Steam",
        IReadOnlySet<string>? GameExecutables = null);
    internal sealed record VdfValue(string? Scalar, Dictionary<string, VdfValue>? Children);

    internal static class VdfParser
    {
        private const int MaxTokens = 50_000;
        private const int MaxDepth = 64;
        private const int MaxStringLength = 4096;

        internal static bool TryParse(string text, out Dictionary<string, VdfValue> root)
        {
            root = new Dictionary<string, VdfValue>(StringComparer.OrdinalIgnoreCase);
            if (text.Length > 1_048_576) return false;
            try
            {
                var parser = new VdfParserImpl(text);
                root = parser.Parse();
                return true;
            }
            catch (FormatException) { return false; }
        }

        private sealed class VdfParserImpl(string text)
        {
            private readonly List<Token> tokens = Tokenize(text);
            private int index;
            private int pairs;

            internal Dictionary<string, VdfValue> Parse()
            {
                if (tokens.Count < 2 || tokens[0].Value is null || tokens[1].Kind != TokenKind.OpenBrace)
                    throw new FormatException("Missing VDF root.");
                var rootName = tokens[0].Value!;
                index = 2;
                var root = ParseBlock(1);
                if (index >= tokens.Count || tokens[index++].Kind != TokenKind.CloseBrace)
                    throw new FormatException("Unclosed VDF root.");
                if (index != tokens.Count) throw new FormatException("Trailing VDF tokens.");
                return new Dictionary<string, VdfValue>(StringComparer.OrdinalIgnoreCase) { [rootName] = new(null, root) };
            }

            private Dictionary<string, VdfValue> ParseBlock(int depth)
            {
                if (depth > MaxDepth) throw new FormatException("VDF nesting limit exceeded.");
                var values = new Dictionary<string, VdfValue>(StringComparer.OrdinalIgnoreCase);
                while (index < tokens.Count && tokens[index].Kind != TokenKind.CloseBrace)
                {
                    var key = tokens[index++].Value ?? throw new FormatException("Expected VDF key.");
                    if (++pairs > MaxTokens || index >= tokens.Count) throw new FormatException("VDF pair limit exceeded.");
                    if (tokens[index].Kind == TokenKind.OpenBrace)
                    {
                        index++;
                        values[key] = new(null, ParseBlock(depth + 1));
                        if (index >= tokens.Count || tokens[index++].Kind != TokenKind.CloseBrace) throw new FormatException("Unclosed VDF object.");
                    }
                    else if (tokens[index].Kind == TokenKind.CloseBrace) throw new FormatException("Missing VDF value.");
                    else values[key] = new(tokens[index++].Value, null);
                }
                if (index >= tokens.Count) throw new FormatException("Unclosed VDF object.");
                return values;
            }

            private static List<Token> Tokenize(string text)
            {
                var result = new List<Token>();
                for (var i = 0; i < text.Length;)
                {
                    var c = text[i];
                    if (char.IsWhiteSpace(c)) { i++; continue; }
                    if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                    { i += 2; while (i < text.Length && text[i] is not '\r' and not '\n') i++; continue; }
                    if (c == '{') { result.Add(new(TokenKind.OpenBrace, null)); i++; }
                    else if (c == '}') { result.Add(new(TokenKind.CloseBrace, null)); i++; }
                    else if (c == '"')
                    {
                        i++;
                        var value = new StringBuilder();
                        var closed = false;
                        while (i < text.Length)
                        {
                            c = text[i++];
                            if (c == '"') { closed = true; break; }
                            if (c == '\\' && i < text.Length)
                            {
                                var escaped = text[i++];
                                value.Append(escaped switch { '"' => '"', '\\' => '\\', 'n' => '\n', 't' => '\t', _ => escaped });
                            }
                            else value.Append(c);
                            if (value.Length > MaxStringLength) throw new FormatException("VDF string too long.");
                        }
                        if (!closed) throw new FormatException("Unclosed VDF string.");
                        result.Add(new(TokenKind.Value, value.ToString()));
                    }
                    else
                    {
                        var start = i;
                        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '{' and not '}') i++;
                        if (i == start) throw new FormatException("Invalid VDF token.");
                        var value = text[start..i];
                        if (value.Length > MaxStringLength) throw new FormatException("VDF token too long.");
                        result.Add(new(TokenKind.Value, value));
                    }
                    if (result.Count > MaxTokens) throw new FormatException("VDF token limit exceeded.");
                }
                return result;
            }

            private enum TokenKind { Value, OpenBrace, CloseBrace }
            private readonly record struct Token(TokenKind Kind, string? Value);
        }
    }
}
