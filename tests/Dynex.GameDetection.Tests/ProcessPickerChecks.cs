using Dynex.Desktop.Games;

internal static class ProcessPickerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var filtered = GameDetector.FilterSelectableProcessNames([
            "VALORANT-Win64-Shipping", "Marvel-Win64-Shipping", "cs2.exe", "CS2",
            "steamwebhelper", "DYNEX", "Dynex.Shaper", @"C:\Games\Game.exe", "bad:name"]);
        check(filtered.SequenceEqual(new[] { "cs2", "Marvel-Win64-Shipping", "VALORANT-Win64-Shipping" }),
            "picker filters paths, invalid names, ignored processes and case/.exe duplicates");

        const string existing = "VALORANT-Win64-Shipping; Marvel-Win64-Shipping; cs2";
        check(GameDetector.AppendConfiguredProcess(existing, "CS2.exe") == existing,
            "picker append keeps existing text when process is already configured");
        check(GameDetector.AppendConfiguredProcess("  cs2.EXE, CustomGame\n", "CS2") == "  cs2.EXE, CustomGame\n",
            "picker recognizes existing executable suffixes and preserves original separators and whitespace");
        check(GameDetector.FilterSelectableProcessNames(["_invalid", new string('a', 81), "name\n", "écran", "Warframe.x64"]).SequenceEqual(["name", "Warframe.x64"]),
            "picker choices satisfy settings name limits and preserve meaningful dots");
        check(GameDetector.AppendConfiguredProcess("", "CustomGame.exe") == "CustomGame",
            "picker fills an empty configuration with a normalized process name");
        check(GameDetector.AppendConfiguredProcess(string.Join("; ", Enumerable.Repeat("a", 1364)), "abcd").Length == 4096,
            "picker permits the exact configuration length boundary");
        check(GameDetector.AppendConfiguredProcess(existing, "Discord") == existing + "; Discord",
            "picker append preserves existing text and adds selected process");
        var appended = GameDetector.AppendConfiguredProcess(existing, "my-game");
        var detected = GameDetector.Detect(
            [new GameDetector.ProcessSnapshot("my-game", 997, null, DateTimeOffset.UtcNow)],
            [], GameDetector.ParseAdditionalNames(appended));
        check(detected is { IsRunning: true, Source: "Configured", GameName: "my-game" },
            "picker append is recognized through configured game detection");

        var atLimit = new string('x', 4096);
        try
        {
            _ = GameDetector.AppendConfiguredProcess(atLimit, "Discord");
            check(false, "picker append rejects configuration beyond the 4096 character limit");
        }
        catch (ArgumentException) { check(true, "picker append rejects configuration beyond the 4096 character limit"); }
    }
}
