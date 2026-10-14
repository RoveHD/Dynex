using Dynex.Desktop.Games;

internal static class ForegroundChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        GameDetectionResult[] games =
        [
            new("First game", "firstgame", 101, started, true, "test"),
            new("Second game", "secondgame", 202, started.AddMinutes(1), true, "test")
        ];
        var foreground = new ForegroundProcess(202, "SECONDGAME", started.AddMinutes(1));
        check(ForegroundGame.Find(games, foreground)?.GameName == "Second game",
            "foreground selection uses the matching game PID instead of the first running title");
        check(ForegroundGame.Find(games, foreground with { ProcessId = 303 }) is null,
            "unknown foreground PID never activates an open background game");
        check(ForegroundGame.Find(games, null) is null &&
            ForegroundGame.Find(games, foreground with { ProcessId = 0 }) is null,
            "missing or invalid foreground ownership stays inactive");
        check(ForegroundGame.Find(games, foreground with { ProcessName = "Discord" }) is null,
            "reused PID with another process name cannot impersonate a game");
        check(ForegroundGame.Find(games, foreground with { StartedAt = started }) is null,
            "reused game PID with a different process start time is rejected");
        check(ForegroundGame.Find(games, foreground with { StartedAt = null }) is null,
            "unreadable foreground start time cannot certify a previously known game identity");
        check(ForegroundGame.Find([games[1] with { ProcessStartedAt = null }], foreground) is not null,
            "games without an available start time still require matching foreground PID and name");
        check(ForegroundGame.Find([games[1] with { IsRunning = false }], foreground) is null &&
            ForegroundGame.Find([games[1] with { ProcessName = null }], foreground) is null &&
            ForegroundGame.Find([games[1] with { GameName = null }], foreground) is null,
            "stopped games and incomplete detection identities cannot enable foreground gaming");
        var excluded = GameDetector.DetectAll(
            [new("wallpaper64", 202, null, started), new("steam", 203, null, started)],
            [], GameDetector.ParseAdditionalNames("wallpaper64;steam"));
        check(ForegroundGame.Find(excluded, foreground with { ProcessName = "wallpaper64", StartedAt = started }) is null,
            "foreground wallpaper and launcher processes remain excluded even when explicitly configured");
        var current = ForegroundGame.Read();
        check(current is null || current is { ProcessId: > 0, ProcessName.Length: > 0 },
            "actual read-only Windows foreground query returns an identity or an unknown state");
    }
}
