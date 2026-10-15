using Dynex.Desktop.Games;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}

var temp = Path.Combine(Path.GetTempPath(), "dynex-game-detection-" + Guid.NewGuid().ToString("N"));
ProcessPickerChecks.Run(Check);
ForegroundChecks.Run(Check);
Directory.CreateDirectory(temp);
try
{
    var steamRoot = Path.Combine(temp, "Steam");
    var extraLibrary = Path.Combine(temp, "Steam Library");
    var traversalLibrary = Path.Combine(temp, "Outside Library");
    var steamApps = Path.Combine(steamRoot, "steamapps");
    var extraApps = Path.Combine(extraLibrary, "steamapps");
    var install = Path.Combine(extraApps, "common", "Example Multiplayer");
    var siblingInstall = install + " Backup";
    Directory.CreateDirectory(steamApps);
    var appInfoPath = Path.Combine(steamRoot, "appcache", "appinfo.vdf");
    Directory.CreateDirectory(Path.GetDirectoryName(appInfoPath)!);
    File.WriteAllBytes(appInfoPath, AppInfo(41, (100, "Game")));
    Directory.CreateDirectory(Path.Combine(install, "bin"));
    Directory.CreateDirectory(siblingInstall);
    Directory.CreateDirectory(Path.Combine(traversalLibrary, "steamapps", "common", "Outside Game"));
    var executable = Path.Combine(install, "bin", "ExampleGame.exe");
    var siblingExecutable = Path.Combine(siblingInstall, "ExampleGame.exe");
    File.WriteAllText(executable, "synthetic test file");
    File.WriteAllText(siblingExecutable, "synthetic test file");

    File.WriteAllText(Path.Combine(steamApps, "libraryfolders.vdf"), $$"""
        "libraryfolders"
        {
          "0" { "path" "{{EscapeVdf(steamRoot)}}" }
          "1" { "path" "{{EscapeVdf(extraLibrary)}}" "apps" { "100" "1" } }
          "2" { "path" "{{EscapeVdf(Path.Combine(extraLibrary, "..", "Outside Library"))}}" }
        }
        """);
    File.WriteAllText(Path.Combine(extraApps, "appmanifest_100.acf"), """
        // A representative Steam manifest with nested, irrelevant fields.
        "AppState"
        {
          "appid" "100"
          "name" "Example Multiplayer"
          "installdir" "Example Multiplayer"
          "UserLocalConfigStore" { "language" "english" }
        }
        """);
    File.WriteAllText(Path.Combine(extraApps, "appmanifest_101.acf"), "not valid VDF");
    File.WriteAllText(Path.Combine(extraApps, "appmanifest_102.acf"), """
        "AppState" { "name" "Traversal" "installdir" "../Escape" }
        """);
    File.WriteAllText(Path.Combine(traversalLibrary, "steamapps", "appmanifest_103.acf"), """
        "AppState" { "name" "Outside Game" "installdir" "Outside Game" }
        """);

    var catalog = GameDetector.ReadSteamCatalog([steamRoot]);
    Check(catalog.Count == 1 && catalog[0].Name == "Example Multiplayer", "discovers game from additional Steam library and skips corrupt/traversal manifests");

    var started = DateTimeOffset.UtcNow.AddMinutes(-3);
    var steamProcess = new GameDetector.ProcessSnapshot("ExampleGame", 120, executable, started);
    var steamDetection = GameDetector.Detect([steamProcess], catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Check(steamDetection is { IsRunning: true, Source: "Steam", GameName: "Example Multiplayer", ProcessName: "ExampleGame", ProcessId: 120, ProcessStartedAt: not null },
        "Steam process detected only from actual app install folder");
    foreach (var helper in new[] { "CrashReportClient.exe", "CrashReportClientEditor.exe" })
    {
        var crashReporter = GameDetector.Detect(
            [new GameDetector.ProcessSnapshot(helper, 122, Path.Combine(install, helper), started)], catalog,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Check(!crashReporter.IsRunning, $"Unreal crash reporter {helper} is excluded inside a Steam game folder");
    }
    Check(GameDetector.Detect([steamProcess], catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) is { IsRunning: true, GameName: "Example Multiplayer" },
        "actual game remains detected after excluding Unreal crash reporters");
    var prefixFalseMatch = GameDetector.Detect(
        [new GameDetector.ProcessSnapshot("ExampleGame", 121, siblingExecutable, started)], catalog,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Check(!prefixFalseMatch.IsRunning, "sibling path with matching string prefix is not inside Steam install folder");

    var expectedGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
        ["Overwatch"] = "Overwatch"
    };
    foreach (var game in expectedGames)
    {
        var detection = GameDetector.Detect(
            [new GameDetector.ProcessSnapshot(game.Key + ".exe", 200, null, started)], [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Check(detection is { IsRunning: true, Source: "BuiltIn" } && detection.GameName == game.Value,
            $"built-in process mapping {game.Key}");
    }

    var allGames = GameDetector.DetectAll([
        steamProcess,
        new GameDetector.ProcessSnapshot("cs2", 201, null, started),
        new GameDetector.ProcessSnapshot("Marvel-Win64-Shipping", 202, null, started),
        new GameDetector.ProcessSnapshot("wallpaper64", 203, null, started)
    ], catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Check(allGames.Count == 3 && allGames.Select(g => g.ProcessId).ToHashSet().SetEquals([120, 201, 202]),
        "all concurrent game identities protected while Wallpaper Engine remains excluded");

    var genericUnreal = GameDetector.Detect(
        [new GameDetector.ProcessSnapshot("UnrealGame", 300, null, started)], [],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Check(!genericUnreal.IsRunning, "generic Unreal process alone does not identify a game");

    var configured = GameDetector.Detect(
        [new GameDetector.ProcessSnapshot("my-game.exe", 400, null, started)], [],
        GameDetector.ParseAdditionalNames("other; My-Game.exe"));
    Check(configured is { IsRunning: true, Source: "Configured", GameName: "my-game", ProcessName: "my-game" },
        "additional process names are case-insensitive and accept an exe suffix");
    var excludedHelper = GameDetector.Detect(
        [new GameDetector.ProcessSnapshot("steamwebhelper", 401, executable, started)], catalog,
        GameDetector.ParseAdditionalNames("steamwebhelper"));
    Check(!excludedHelper.IsRunning, "Steam web helper is always excluded");
    Check(GameDetector.Detect([], [], GameDetector.ParseAdditionalNames("")) == GameDetectionResult.None,
        "no process returns the explicit empty result");
    var processScans = 0;
    var rootScans = 0;
    var cachedDetector = new GameDetector(() => { rootScans++; return []; }, () => { processScans++; return []; });
    cachedDetector.ScanCore("");
    cachedDetector.ScanCore("");
    Check(rootScans == 1 && processScans == 2, "Steam metadata is cached while process detection runs on every scan");

    Check(!GameDetector.VdfParser.TryParse("\"AppState\" { \"name\" \"unfinished\"", out _), "corrupt unclosed VDF rejected");
    Check(!GameDetector.VdfParser.TryParse(new string('x', 1_048_577), out _), "oversized VDF rejected before parsing");

    var moreApps = new (uint Id, string Kind, string Title, string Folder)[]
    {
        (200, "Application", "Unknown Creative Suite", "Creative Suite"),
        (201, "Tool", "Background Companion", "Background Companion"),
        (202, "Video", "Video Program", "Video Program"),
        (203, "DLC", "Download Content", "Download Content"),
        (204, "Demo", "Demonstration", "Demonstration"),
        (205, "FutureUnknownKind", "Future App", "Future App"),
        (210, "Game", "Wallpaper Adventures: A Real Game", "Wallpaper Adventures"),
        (431960, "Application", "Wallpaper Engine", "wallpaper_engine")
    };
    foreach (var app in moreApps)
    {
        Directory.CreateDirectory(Path.Combine(extraApps, "common", app.Folder));
        File.WriteAllText(Path.Combine(extraApps, $"appmanifest_{app.Id}.acf"),
            $"\"AppState\" {{ \"appid\" \"{app.Id}\" \"name\" \"{app.Title}\" \"installdir\" \"{app.Folder}\" }}");
    }
    var metadata = new[] { (100u, "Game") }.Concat(moreApps.Select(a => (a.Id, a.Kind))).ToArray();
    File.WriteAllBytes(appInfoPath, AppInfo(41, metadata));
    catalog = GameDetector.ReadSteamCatalog([steamRoot]);
    Check(catalog.Count == 3 && catalog.Any(g => g.Name == "Wallpaper Adventures: A Real Game") && catalog.Any(g => g.Name == "Demonstration"),
        "positively classified Steam Games and playable Demos enter catalog; game names containing Wallpaper remain valid");
    foreach (var app in moreApps.Where(a => a.Kind is not "Game" and not "Demo"))
    {
        var renamed = new GameDetector.ProcessSnapshot("UnknownRenamedWorker", 10, Path.Combine(extraApps, "common", app.Folder, "arbitrary.exe"), started);
        Check(!GameDetector.Detect([renamed], catalog, GameDetector.ParseAdditionalNames("")).IsRunning,
            $"Steam {app.Kind} cannot become a game through an unknown or renamed executable");
    }
    foreach (var renderer in new[] { "wallpaper32", "wallpaper64", "webwallpaper32", "wallpaperui" })
    {
        Check(!GameDetector.Detect([new GameDetector.ProcessSnapshot(renderer, 5, executable, started)], catalog,
            GameDetector.ParseAdditionalNames(renderer)).IsRunning, $"exact Wallpaper Engine renderer {renderer} is excluded even as configured name");
    }
    var unknownGame = new GameDetector.ProcessSnapshot("UnlistedAdventure", 2000,
        Path.Combine(extraApps, "common", "Wallpaper Adventures", "unlisted.exe"), started);
    var wallpaper = new GameDetector.ProcessSnapshot("RenamedWallpaperProgram", 1,
        Path.Combine(extraApps, "common", "wallpaper_engine", "renamed.exe"), started);
    var tool = new GameDetector.ProcessSnapshot("AnotherArbitraryName", 2,
        Path.Combine(extraApps, "common", "Creative Suite", "renamed.exe"), started);
    Check(GameDetector.Detect([wallpaper, tool, unknownGame], catalog, GameDetector.ParseAdditionalNames("")) is
        { IsRunning: true, GameName: "Wallpaper Adventures: A Real Game", ProcessId: 2000 },
        "actual unknown Steam game wins while Wallpaper Engine and another application run concurrently");
    GameCoverageChecks.Run(Check, temp);
    LauncherChecks.Run(Check, temp);
    ProcessTableChecks.Run(Check);

    File.WriteAllBytes(appInfoPath, AppInfo(41, (431960, "Game"), (100, "Game")));
    catalog = GameDetector.ReadSteamCatalog([steamRoot]);
    Check(catalog.Count == 1 && !GameDetector.Detect([wallpaper], catalog, GameDetector.ParseAdditionalNames("")).IsRunning,
        "Wallpaper Engine app ID 431960 is excluded even if cache incorrectly reports Game");
    foreach (var version in new[] { 39, 40, 41 })
    {
        var parsed = SteamAppInfoReader.Parse(AppInfo(version, (100, "Game"), (200, "Application"), (201, "Tool")));
        Check(parsed.Count == 3 && parsed[100] == "Game" && parsed[200] == "Application" && parsed[201] == "Tool",
            $"appinfo v{version} reads common/type and ignores misleading config/type=Game");
    }
    var future = AppInfo(41, (100, "Game"));
    BinaryPrimitives.WriteUInt32LittleEndian(future, 0x0756442A);
    Check(SteamAppInfoReader.Parse(future).Count == 0, "unknown appinfo version does not imply Game");
    var invalidOffset = AppInfo(41, (100, "Game"));
    BinaryPrimitives.WriteInt64LittleEndian(invalidOffset.AsSpan(8), long.MaxValue);
    Check(SteamAppInfoReader.Parse(invalidOffset).Count == 0, "out-of-bounds string table rejected");
    var invalidIndex = AppInfo(41, (100, "Game"));
    BinaryPrimitives.WriteUInt32LittleEndian(invalidIndex.AsSpan(85), uint.MaxValue);
    RehashFirstEntry(invalidIndex);
    Check(SteamAppInfoReader.Parse(invalidIndex).Count == 0, "out-of-bounds pooled key rejected even with matching blob checksum");
    var corrupted = AppInfo(41, (100, "Game"), (210, "Game"));
    corrupted[90] ^= 1;
    var goodRemainder = SteamAppInfoReader.Parse(corrupted);
    Check(!goodRemainder.ContainsKey(100) && goodRemainder.TryGetValue(210, out var intact) && intact == "Game",
        "torn/corrupt blob checksum excludes its entry and preserves independent valid entries");
    Check(!SteamAppInfoReader.Parse(AppInfo(41, (100, "Game"), (100, "Application"))).TryGetValue(100, out var duplicate) || duplicate != "Game",
        "conflicting duplicate app IDs cannot positively classify as Game");
    var corruptDuplicate = AppInfo(41, (100, "Game"), (100, "Application"));
    var secondEntryAt = 24 + BinaryPrimitives.ReadInt32LittleEndian(corruptDuplicate.AsSpan(20));
    corruptDuplicate[secondEntryAt + 68] ^= 1;
    Check(SteamAppInfoReader.Parse(corruptDuplicate).TryGetValue(100, out var invalidDuplicate) && invalidDuplicate == "Unknown",
        "duplicate ID with corrupt second blob cannot retain an earlier Game classification");
    var complete = AppInfo(41, (100, "Game"));
    File.WriteAllBytes(appInfoPath, complete[..^4]);
    Check(GameDetector.ReadSteamCatalog([steamRoot]).Count == 0, "truncated updating cache publishes no partial game catalog");
    File.WriteAllBytes(appInfoPath, complete);
    Check(GameDetector.ReadSteamCatalog([steamRoot]).Count == 1, "complete replacement cache restores positive detection");
    using (var oversized = new FileStream(appInfoPath, FileMode.Create, FileAccess.Write)) oversized.SetLength(SteamAppInfoReader.MaxFileBytes + 1L);
    Check(SteamAppInfoReader.ReadTypes(appInfoPath).Count == 0, "oversized cache rejected before allocation");
    File.Delete(appInfoPath);
    catalog = GameDetector.ReadSteamCatalog([steamRoot]);
    Check(catalog.Count == 0 && !GameDetector.Detect([wallpaper, tool], catalog, GameDetector.ParseAdditionalNames("")).IsRunning,
        "missing appinfo cannot classify installed applications as games");
    Check(GameDetector.Detect([new GameDetector.ProcessSnapshot("Marvel-Win64-Shipping", 77, null, started)], catalog,
        GameDetector.ParseAdditionalNames("")) is { IsRunning: true, Source: "BuiltIn", GameName: "Marvel Rivals" },
        "known built-in games remain available without Steam metadata");

    if (args is ["--inspect-cache", var liveCache])
    {
        var liveTypes = SteamAppInfoReader.ReadTypes(liveCache);
        foreach (var grouping in liveTypes.Values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            Console.WriteLine($"LOCAL CACHE TYPE: {grouping.Key} = {grouping.Count()}");
        Console.WriteLine($"LOCAL WALLPAPER 431960: {(liveTypes.TryGetValue(431960, out var wallpaperType) ? wallpaperType : "not cached")}");
        var liveRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(liveCache)))!;
        var liveCatalog = GameDetector.ReadSteamCatalog([liveRoot]);
        var liveDetected = new GameDetector(() => [liveRoot], () => []).GetCatalog();
        Console.WriteLine($"LOCAL GAME CATALOG COUNT: {liveCatalog.Count}; cached catalog count: {liveDetected.Count}");
    }
    if (args is ["--inspect-local"])
    {
        var localSteam = GameDetector.ReadSteamCatalog(GameDetector.FindSteamRoots());
        var localEpic = GameDetector.ReadEpicCatalog(GameDetector.FindEpicManifestRoots());
        var localGog = GameDetector.ReadGogCatalog(GameDetector.FindGogInstallRoots());
        Console.WriteLine($"LOCAL POSITIVE CATALOGS: Steam={localSteam.Count}, Epic={localEpic.Count}, GOG={localGog.Count}");
        foreach (var group in GameDetector.ScanAll("").GroupBy(game => game.Source))
            Console.WriteLine($"LOCAL RUNNING GAME IDENTITIES: {group.Key}={group.Count()}");
    }
}
finally
{
    Directory.Delete(temp, recursive: true);
}

Console.WriteLine($"Game detection: {passed} checks passed");

static string EscapeVdf(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

static byte[] AppInfo(int version, params (uint Id, string Kind)[] apps)
{
    var keys = new[] { "appinfo", "common", "type", "name", "config" };
    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
    writer.Write(0x07564400u | (uint)version); writer.Write(1u);
    if (version == 41) writer.Write(0L);
    foreach (var app in apps)
    {
        using var payload = new MemoryStream();
        using var kv = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
        void CString(string value) { kv.Write(Encoding.UTF8.GetBytes(value)); kv.Write((byte)0); }
        void Key(string value)
        {
            if (version == 41) kv.Write(Array.IndexOf(keys, value));
            else CString(value);
        }
        void Pair(byte tag, string key) { kv.Write(tag); Key(key); }
        Pair(0, "appinfo"); Pair(0, "common"); Pair(1, "type"); CString(app.Kind);
        Pair(1, "name"); CString("A title with misleading Game words"); kv.Write((byte)8);
        Pair(0, "config"); Pair(1, "type"); CString("Game"); kv.Write((byte)8);
        kv.Write((byte)8); kv.Write((byte)8); kv.Flush();
        var bytes = payload.ToArray();
        writer.Write(app.Id); writer.Write((uint)(bytes.Length + (version >= 40 ? 60 : 40)));
        writer.Write(new byte[40]);
        if (version >= 40) writer.Write(SHA1.HashData(bytes));
        writer.Write(bytes);
    }
    writer.Write(0u);
    if (version == 41)
    {
        var tableAt = output.Position;
        writer.Write((uint)keys.Length);
        foreach (var key in keys) { writer.Write(Encoding.UTF8.GetBytes(key)); writer.Write((byte)0); }
        writer.Flush(); output.Position = 8; writer.Write(tableAt);
    }
    writer.Flush();
    return output.ToArray();
}

static void RehashFirstEntry(byte[] bytes)
{
    var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
    SHA1.HashData(bytes.AsSpan(84, size - 60)).CopyTo(bytes.AsSpan(64, 20));
}
