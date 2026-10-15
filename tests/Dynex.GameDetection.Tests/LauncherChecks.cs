using Dynex.Desktop.Games;

internal static class LauncherChecks
{
    internal static void Run(Action<bool, string> check, string temp)
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var ubisoftDir = Path.Combine(temp, "Ubisoft Game");
        var blizzardDir = Path.Combine(temp, "Blizzard Game");
        var launcherDir = Path.Combine(temp, "Battle.net");
        foreach (var dir in new[] { ubisoftDir, blizzardDir, launcherDir }) Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(ubisoftDir, "AnyGame.exe"), "");
        var registered = GameDetector.ReadRegisteredInstalls([
            ("Assassin's Creed Test", ubisoftDir, "Ubisoft"), ("Overwatch Test", blizzardDir, "Battle.net"),
            ("Battle.net", launcherDir, "Battle.net"), ("Root Drive", Path.GetPathRoot(temp)!, "EA"), ("Bad\u0001Name", ubisoftDir, "EA")]);
        check(registered.Count == 2 && registered.Any(g => g is { Source: "Ubisoft", Name: "Assassin's Creed Test" }) &&
            registered.Any(g => g.Source == "Battle.net"), "Ubisoft/Battle.net installs are recognized; launchers, drive roots and invalid names are not");
        var exe = Path.Combine(ubisoftDir, "AnyGame.exe");
        var ubisoftResult = GameDetector.Detect([new("AnyGame", 900, exe, started)], registered, GameDetector.ParseAdditionalNames(""));
        check(ubisoftResult is { IsRunning: true, Source: "Ubisoft", GameName: "Assassin's Creed Test" }, "process below a registered Ubisoft install is a game");

        var xboxRoot = Path.Combine(temp, "XboxGames");
        var content = Path.Combine(xboxRoot, "Forza Test", "Content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "ForzaTest.exe"), "");
        File.WriteAllText(Path.Combine(content, "Helper.exe"), "");
        File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"),
            """<?xml version="1.0" encoding="utf-8"?><Game configVersion="1"><ExecutableList><Executable Name="ForzaTest.exe" Id="Game"/></ExecutableList><ShellVisuals DefaultDisplayName="Forza Test"/></Game>""");
        var hostile = Path.Combine(xboxRoot, "Hostile", "Content");
        Directory.CreateDirectory(hostile);
        File.WriteAllText(Path.Combine(hostile, "MicrosoftGame.config"),
            """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><Game><ExecutableList><Executable Name="&e;"/></ExecutableList></Game>""");
        var xbox = GameDetector.ReadXboxCatalog([xboxRoot]);
        check(xbox is [{ Source: "Xbox", Name: "Forza Test" }], "Xbox config with listed executable is recognized; DTD/entity configs are rejected");
        var forza = GameDetector.Detect([new("ForzaTest", 901, Path.Combine(content, "ForzaTest.exe"), started)], xbox, GameDetector.ParseAdditionalNames(""));
        var helper = GameDetector.Detect([new("Helper", 902, Path.Combine(content, "Helper.exe"), started)], xbox, GameDetector.ParseAdditionalNames(""));
        check(forza.IsRunning && !helper.IsRunning, "only executables listed in MicrosoftGame.config count as the Xbox game");
    }
}
