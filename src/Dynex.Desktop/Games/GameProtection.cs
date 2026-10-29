namespace Dynex.Desktop.Games;

/// <summary>Games eligible for network protection; actual packet activity is checked by the runtime.</summary>
internal static class GameProtection
{
    internal static IReadOnlyList<GameDetectionResult> FindCandidates(IReadOnlyList<GameDetectionResult> games) =>
        games.Where(game => game.IsRunning && !string.IsNullOrWhiteSpace(game.GameName) &&
                !string.IsNullOrWhiteSpace(game.ProcessName))
            .DistinctBy(game => (game.ProcessId, game.ProcessStartedAt)).ToArray();
}
