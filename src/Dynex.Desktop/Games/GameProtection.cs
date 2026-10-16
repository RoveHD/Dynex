namespace Dynex.Desktop.Games;

/// <summary>Games eligible for network protection; actual packet activity is checked by the runtime.</summary>
internal static class GameProtection
{
    internal static IReadOnlyList<GameDetectionResult> FindCandidates(IReadOnlyList<GameDetectionResult> games,
        ForegroundProcess? foreground, IReadOnlySet<string> earlyNames)
    {
        var focused = ForegroundGame.Find(games, foreground);
        return (focused is null ? Enumerable.Empty<GameDetectionResult>() : [focused])
            .Concat(games.Where(game => game.IsRunning && game.ProcessName is { } name && earlyNames.Contains(name)))
            .DistinctBy(game => (game.ProcessId, game.ProcessStartedAt)).ToArray();
    }
}
