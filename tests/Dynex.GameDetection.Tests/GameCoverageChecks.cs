using Dynex.Desktop.Games;
using System.Text;
using System.Text.Json;

internal static class GameCoverageChecks
{
    internal static void Run(Action<bool, string> check, string temp)
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        var expectedGames = new Dictionary<string, string>
        {
            ["Wow"] = "World of Warcraft", ["WowClassic"] = "World of Warcraft Classic",
            ["WowClassicT"] = "World of Warcraft Classic", ["Diablo IV"] = "Diablo IV", ["Diablo III"] = "Diablo III",
            ["SC2_x64"] = "StarCraft II", ["HeroesOfTheStorm_x64"] = "Heroes of the Storm",
            ["Minecraft.Windows"] = "Minecraft", ["RobloxPlayerBeta"] = "Roblox", ["EscapeFromTarkov"] = "Escape from Tarkov",
            ["Warframe.x64"] = "Warframe", ["destiny2"] = "Destiny 2", ["RainbowSix"] = "Rainbow Six Siege",
            ["RainbowSix_Vulkan"] = "Rainbow Six Siege", ["RainbowSix_DX11"] = "Rainbow Six Siege",
            ["GTA5"] = "Grand Theft Auto V", ["GTA5_Enhanced"] = "Grand Theft Auto V Enhanced"
        };
        foreach (var (process, title) in expectedGames)
        foreach (var suffix in new[] { "", ".exe" })
        {
            var result = Detect(process + suffix);
            check(result is { IsRunning: true, Source: "BuiltIn" } && result.GameName == title && result.ProcessStartedAt == started,
                $"additional built-in {process}{suffix} retains identity and title");
        }

        foreach (var process in new[] { "javaw", "RobloxStudioBeta", "Battle.net", "RiotClientServices", "EpicGamesLauncher", "GalaxyClient", "MinecraftLauncher", "UnrealEditor" })
            check(!Detect(process).IsRunning, $"shared VM, game editor or launcher {process} alone is not a game");
        var dotName = GameDetector.Detect([new("my.game.client", 501, null, started)], [], GameDetector.ParseAdditionalNames("my.game.client.exe"));
        check(dotName is { IsRunning: true, ProcessName: "my.game.client" }, "dots in extensionless process names are retained for configured games");

        var manifests = Path.Combine(temp, "epic-manifests");
        var install = Path.Combine(temp, "Epic Unknown Multiplayer");
        var launch = Path.Combine(install, "UnknownGame.exe");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(install);
        File.WriteAllText(launch, "fixture");
        var epic = new Dictionary<string, object?>
        {
            ["AppName"] = "UnknownGameApp", ["DisplayName"] = "Unknown Epic Multiplayer", ["InstallLocation"] = install,
            ["LaunchExecutable"] = "UnknownGame.exe", ["AppCategories"] = new[] { "public", "games", "applications" },
            ["MainGameAppName"] = "UnknownGameApp", ["bIsApplication"] = true, ["bIsExecutable"] = true, ["bIsIncompleteInstall"] = false
        };
        var manifest = Path.Combine(manifests, "installed.item");
        SaveEpic();
        var epicCatalog = GameDetector.ReadEpicCatalog([manifests]);
        check(epicCatalog.Count == 1 && epicCatalog[0].Source == "Epic", "positive Epic games category discovers unknown installed game");
        var resultEpic = GameDetector.Detect([new("UnknownGame", 502, launch, started)], epicCatalog, GameDetector.ParseAdditionalNames(""));
        check(resultEpic is { IsRunning: true, Source: "Epic", GameName: "Unknown Epic Multiplayer", ProcessId: 502 } && resultEpic.ProcessStartedAt == started,
            "unknown Epic game gets exact PID and start identity without a built-in name");
        var actualChild = Path.Combine(install, "Game", "Binaries", "Win64", "Unknown-Win64-Shipping.exe");
        check(GameDetector.Detect([new("Unknown-Win64-Shipping", 503, actualChild, started)], epicCatalog, GameDetector.ParseAdditionalNames("")).IsRunning,
            "Epic Unreal game binary below positively classified install is recognized after bootstrap exits");
        check(!GameDetector.Detect([new("UnknownGame", 504, Path.Combine(install + " Backup", "UnknownGame.exe"), started)], epicCatalog, GameDetector.ParseAdditionalNames("")).IsRunning,
            "Epic sibling directory cannot borrow the game identity");
        foreach (var helper in new[] { "Launcher", "UnityCrashHandler64", "EasyAntiCheat_EOS", "UnrealEditor", "EpicWebHelper" })
            check(!GameDetector.Detect([new(helper, 505, Path.Combine(install, helper + ".exe"), started)], epicCatalog, GameDetector.ParseAdditionalNames("")).IsRunning,
                $"Epic supporting process {helper} is excluded inside a valid game install");
        foreach (var directory in new[] { "_CommonRedist", "DirectX", "redist", "EasyAntiCheat", "BattlEye", ".egstore" })
            check(!GameDetector.Detect([new("UnlistedUtility", 505, Path.Combine(install, directory, "UnlistedUtility.exe"), started)], epicCatalog, GameDetector.ParseAdditionalNames("")).IsRunning,
                $"non-game support directory {directory} is excluded inside a valid game install");
        foreach (var categories in new[] { new[] { "applications" }, new[] { "engines/ue5", "engines" }, new[] { "plugins/engine", "plugins" }, Array.Empty<string>() })
        {
            epic["AppCategories"] = categories;
            SaveEpic();
            check(GameDetector.ReadEpicCatalog([manifests]).Count == 0, "Epic engine, plugin or unclassified application has no positive games category: " + string.Join(',', categories));
        }
        epic["AppCategories"] = new[] { "games", "applications" };
        foreach (var (key, badValue) in new (string, object?)[]
                 { ("bIsIncompleteInstall", true), ("bIsApplication", false), ("bIsExecutable", false), ("MainGameAppName", "DifferentBaseGame"),
                   ("LaunchExecutable", "../outside.exe"), ("LaunchExecutable", launch), ("LaunchExecutable", "https://example.invalid/launch"),
                   ("LaunchExecutable", "missing.exe"), ("InstallLocation", Path.GetPathRoot(install)), ("DisplayName", "bad\ntitle") })
        {
            var previous = epic[key];
            epic[key] = badValue;
            SaveEpic();
            check(GameDetector.ReadEpicCatalog([manifests]).Count == 0, $"unsafe or non-game Epic metadata is rejected: {key}={badValue}");
            epic[key] = previous;
        }
        SaveEpic();
        File.WriteAllText(manifest, File.ReadAllText(manifest)[..^1]);
        check(GameDetector.ReadEpicCatalog([manifests]).Count == 0, "torn Epic JSON is skipped without publishing a game");
        SaveEpic();
        var validJson = File.ReadAllText(manifest);
        File.WriteAllText(manifest, validJson[..^1] + ",\"AppCategories\":[\"games\"]}");
        check(GameDetector.ReadEpicCatalog([manifests]).Count == 0, "duplicate Epic classification fields are rejected");
        SaveEpic();
        File.WriteAllText(manifest, File.ReadAllText(manifest), new UTF8Encoding(true));
        check(GameDetector.ReadEpicCatalog([manifests]).Count == 1, "UTF-8 BOM launcher metadata is accepted");
        using (var oversized = new FileStream(manifest, FileMode.Create, FileAccess.Write)) oversized.SetLength(1_048_577);
        check(GameDetector.ReadEpicCatalog([manifests]).Count == 0, "oversized Epic metadata is rejected before JSON allocation");
        SaveEpic();

        var gogInstall = Path.Combine(temp, "GOG Unknown Multiplayer");
        Directory.CreateDirectory(gogInstall);
        File.WriteAllText(Path.Combine(gogInstall, "NativeGame.exe"), "fixture");
        File.WriteAllText(Path.Combine(gogInstall, "GameLauncher.exe"), "fixture");
        File.WriteAllText(Path.Combine(gogInstall, "Editor.exe"), "fixture");
        var gog = new Dictionary<string, object?>
        {
            ["gameId"] = "1234567890", ["rootGameId"] = "1234567890", ["name"] = "Unknown GOG Multiplayer",
            ["playTasks"] = new object[]
            {
                new { category = "launcher", type = "FileTask", path = "GameLauncher.exe", isPrimary = true },
                new { category = "game", type = "FileTask", path = "NativeGame.exe", isHidden = true },
                new { category = "tool", type = "FileTask", path = "Editor.exe" },
                new { category = "game", type = "URLTask", link = "https://example.invalid/" }
            }
        };
        var gogManifest = Path.Combine(gogInstall, "goggame-1234567890.info");
        SaveGog();
        var gogCatalog = GameDetector.ReadGogCatalog([gogInstall]);
        check(gogCatalog.Count == 1 && gogCatalog[0].Source == "GOG", "GOG positive game FileTask discovers an unknown game");
        check(GameDetector.Detect([new("NativeGame", 600, Path.Combine(gogInstall, "NativeGame.exe"), started)], gogCatalog, GameDetector.ParseAdditionalNames("")) is
            { IsRunning: true, Source: "GOG", GameName: "Unknown GOG Multiplayer" }, "GOG actual game executable is recognized");
        foreach (var name in new[] { "GameLauncher", "Editor", "UnlistedProgram" })
            check(!GameDetector.Detect([new(name, 601, Path.Combine(gogInstall, name + ".exe"), started)], gogCatalog, GameDetector.ParseAdditionalNames("")).IsRunning,
                $"GOG {name} cannot become a game through the shared install directory");
        var originalTasks = gog["playTasks"];
        foreach (var invalidPath in new[] { "../NativeGame.exe", Path.Combine(gogInstall, "NativeGame.exe"), "NativeGame.cmd", "missing.exe" })
        {
            gog["playTasks"] = new[] { new { category = "game", type = "FileTask", path = invalidPath } };
            SaveGog();
            check(GameDetector.ReadGogCatalog([gogInstall]).Count == 0, "GOG unsafe, missing or non-executable game task is rejected: " + invalidPath);
        }
        gog["playTasks"] = originalTasks;
        gog["rootGameId"] = "";
        SaveGog();
        check(GameDetector.ReadGogCatalog([gogInstall]).Count == 1, "older GOG manifest with empty optional rootGameId retains its actual game task");
        gog.Remove("rootGameId");
        SaveGog();
        check(GameDetector.ReadGogCatalog([gogInstall]).Count == 1, "older GOG manifest without optional rootGameId retains its actual game task");
        gog["rootGameId"] = "9876543210";
        SaveGog();
        check(GameDetector.ReadGogCatalog([gogInstall]).Count == 0, "GOG DLC metadata cannot classify its base directory as a separate game");
        gog["rootGameId"] = "1234567890";
        gog["gameId"] = "9999";
        SaveGog();
        check(GameDetector.ReadGogCatalog([gogInstall]).Count == 0, "GOG file ID must match the manifest game ID");
        gog["gameId"] = "1234567890";
        SaveGog();
        File.WriteAllText(gogManifest, "{broken");
        check(GameDetector.ReadGogCatalog([gogInstall]).Count == 0, "malformed GOG metadata is ignored");
        SaveGog();

        var steamScans = 0;
        var epicScans = 0;
        var gogScans = 0;
        var processScans = 0;
        var identity = started;
        var detector = new GameDetector(() => { steamScans++; return []; },
            () => { processScans++; return [new("UnknownGame", 700, launch, identity)]; },
            () => { epicScans++; return [manifests]; }, () => { gogScans++; return [gogInstall]; });
        var first = detector.ScanCore("");
        identity = started.AddSeconds(1);
        var second = detector.ScanCore("");
        check(steamScans == 1 && epicScans == 1 && gogScans == 1 && processScans == 2,
            "all launcher catalogs are cached together while live process identities refresh each scan");
        check(first.ProcessId == second.ProcessId && first.ProcessStartedAt != second.ProcessStartedAt && second.ProcessStartedAt == identity,
            "reused PID gets its new start identity rather than a cached game process identity");

        GameDetectionResult Detect(string name) => GameDetector.Detect([new(name, 500, null, started)], [], GameDetector.ParseAdditionalNames(""));
        void SaveEpic() => File.WriteAllText(manifest, JsonSerializer.Serialize(epic));
        void SaveGog() => File.WriteAllText(gogManifest, JsonSerializer.Serialize(gog));
    }
}
