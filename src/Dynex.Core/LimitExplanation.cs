namespace Dynex.Core;

/// <summary>Why this PC's large downloads are (not) limited, phrased for the overview.</summary>
public static class LimitExplanation
{
    public static string Describe(LimitDecision decision, bool localGaming, string? localGame,
        IReadOnlyList<PeerAdvertisement> remote, bool capacityMeasured, bool speedTestRunning,
        bool driverEnabled, bool driverActive, double? manualLimitMbps, double? appliedLimitMbps)
    {
        if (speedTestRunning) return "Speedtest läuft · Begrenzung pausiert";
        if (!decision.GamingActive) return "Kein Spiel im Fokus · kein Limit";
        var gamers = new List<string>();
        if (localGaming) gamers.Add(string.IsNullOrWhiteSpace(localGame) ? "diesem PC" : $"diesem PC ({localGame})");
        gamers.AddRange(remote.Where(p => p.IsGaming).OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(p => string.IsNullOrWhiteSpace(p.Game) ? p.DeviceName : $"{p.DeviceName} ({p.Game})"));
        var who = gamers.Count switch
        {
            0 => "Gaming im LAN erkannt",
            1 => $"Gaming auf {gamers[0]} erkannt",
            _ => $"Gaming auf {string.Join(", ", gamers[..^1])} und {gamers[^1]} erkannt"
        };
        if (!capacityMeasured) return who + " · kein Limit, bis ein Speedtest gespeichert ist";
        if (!driverEnabled) return who + " · Begrenzung in den Einstellungen ausgeschaltet";
        if (decision.DownloadLimitMbps is null) return who;
        var details = new List<string> { who };
        if (manualLimitMbps is { } manual && Math.Abs(decision.DownloadLimitMbps.Value - manual) < 0.005)
            details.Add("manuelle Obergrenze");
        else if (decision.AdaptiveReduced) details.Add("Ping gestiegen, stärker gebremst");
        else if (decision.ParticipantCount > 1) details.Add($"Budget geteilt auf {decision.ParticipantCount} PCs");
        if (!driverActive || appliedLimitMbps is null) details.Add("Begrenzung wird noch aktiviert");
        return string.Join(" · ", details);
    }
}
