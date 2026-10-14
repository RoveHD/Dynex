using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dynex.Desktop.Games;

internal sealed record ForegroundProcess(int ProcessId, string ProcessName, DateTimeOffset? StartedAt);

/// <summary>Read-only Windows foreground ownership; no hooks or interaction with game windows.</summary>
internal static class ForegroundGame
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    internal static ForegroundProcess? Read()
    {
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0 || pid is 0 or > int.MaxValue)
            return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            DateTimeOffset? startedAt = null;
            try { startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
            catch (Win32Exception) { }
            // A focus switch or process exit while resolving identity is an unknown foreground.
            if (process.HasExited || GetForegroundWindow() != window ||
                GetWindowThreadProcessId(window, out var currentPid) == 0 || currentPid != pid)
                return null;
            return new((int)pid, name, startedAt);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        { return null; }
    }

    internal static GameDetectionResult? Find(IReadOnlyList<GameDetectionResult> games, ForegroundProcess? foreground)
    {
        if (foreground is not { ProcessId: > 0 } || string.IsNullOrWhiteSpace(foreground.ProcessName)) return null;
        return games.FirstOrDefault(game => game.IsRunning && game.ProcessId == foreground.ProcessId &&
            !string.IsNullOrWhiteSpace(game.GameName) && !string.IsNullOrWhiteSpace(game.ProcessName) &&
            string.Equals(game.ProcessName, foreground.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            (game.ProcessStartedAt is null || game.ProcessStartedAt == foreground.StartedAt));
    }
}
