namespace Dynex.Core;

/// <summary>Why this PC's large downloads are (not) limited, phrased for the overview.</summary>
public static class LimitExplanation
{
    public static string Describe(LimitDecision decision, bool localGaming, string? localGame,
        IReadOnlyList<PeerAdvertisement> remote, bool capacityMeasured, bool speedTestRunning,
        bool driverEnabled, bool driverActive, double? manualLimitMbps, double? appliedLimitMbps, DateTimeOffset? pausedUntil = null)
    {
        string? release = driverActive && appliedLimitMbps is { } active
            ? $"Freigabe läuft · aktuell noch {active:0.#} Mbit/s aktiv"
            : null;

        if (speedTestRunning) return release is null ? "Speedtest läuft · Begrenzung pausiert" : $"Speedtest läuft · {release}";
        if (pausedUntil is { } until) return release is null
            ? $"Von dir pausiert bis {until.ToLocalTime():HH:mm} Uhr · Downloads frei"
            : $"Von dir pausiert bis {until.ToLocalTime():HH:mm} Uhr · {release}";
        if (!decision.GamingActive) return release is null
            ? "Kein Spiel im Fokus · kein Limit"
            : $"Kein Spiel im Fokus · {release}";
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
        if (!capacityMeasured) return release is null
            ? who + " · kein Limit, bis ein Speedtest gespeichert ist"
            : $"{who} · kein neues Limit, bis ein Speedtest gespeichert ist · {release}";
        if (!driverEnabled) return release is null
            ? who + " · Begrenzung in den Einstellungen ausgeschaltet"
            : $"{who} · Begrenzung in den Einstellungen ausgeschaltet · {release}";
        if (decision.DownloadLimitMbps is null) return who;
        var details = new List<string> { who };
        if (manualLimitMbps is { } manual && Math.Abs(decision.DownloadLimitMbps.Value - manual) < 0.005)
            details.Add("manuelle Obergrenze");
        else if (decision.AdaptiveReduced) details.Add("Ping gestiegen, stärker gebremst");
        else if (decision.ParticipantCount > 1) details.Add((decision.DownloadActivityComplete
            ? "Gemeinsames Budget nach aktiven Downloads"
            : $"Budget vorläufig auf {decision.ParticipantCount} Geräte geteilt") +
            (decision.LocalShareWeight > 1 ? ", hohe Priorität" : decision.LocalShareWeight < 1 ? ", niedrige Priorität" : ""));
        if (!driverActive || appliedLimitMbps is null) details.Add("Begrenzung wird noch aktiviert");
        return string.Join(" · ", details);
    }
}
