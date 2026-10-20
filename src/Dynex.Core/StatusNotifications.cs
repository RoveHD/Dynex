namespace Dynex.Core;

public sealed record StatusNotification(string Title, string Text);

/// <summary>
/// Turns consecutive status snapshots into a few user-facing notifications. Only transitions
/// notify, and each kind is rate limited so a flapping WLAN cannot flood the notification area.
/// </summary>
public sealed class StatusNotifications
{
    private readonly TimeSpan _cooldown;
    private readonly Dictionary<string, DateTimeOffset> _lastByKind = new();
    private HashSet<string>? _peers;
    private bool _priorityActive;
    private ConnectionProblemKind _problem;

    public StatusNotifications(TimeSpan? cooldown = null) => _cooldown = cooldown ?? TimeSpan.FromSeconds(60);

    public IReadOnlyList<StatusNotification> Observe(IReadOnlyCollection<string> peerNames, bool priorityActive, string priorityReason,
        ConnectionAdvice? advice, DateTimeOffset now)
    {
        var result = new List<StatusNotification>();
        var peers = peerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_peers is { } previous) // The first snapshot only establishes the baseline.
        {
            foreach (var name in peers.Except(previous).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                Add(result, "peer+" + name, now, new("PC verbunden", $"„{name}“ ist jetzt mit DYNEX verbunden."));
            foreach (var name in previous.Except(peers).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                Add(result, "peer-" + name, now, new("PC nicht mehr verbunden", $"„{name}“ antwortet nicht mehr. DYNEX verbindet automatisch neu, sobald der PC wieder erreichbar ist."));
            if (priorityActive && !_priorityActive)
                Add(result, "priority", now, new("Paketpriorisierung aktiv", priorityReason));
            var kind = advice?.Kind ?? ConnectionProblemKind.None;
            if (kind != _problem && kind is ConnectionProblemKind.VersionMismatch or ConnectionProblemKind.FirewallMissing
                or ConnectionProblemKind.NoPrivateNetwork or ConnectionProblemKind.WrongNetwork)
                Add(result, "problem-" + kind, now, new("Verbindungsproblem", advice!.ToString()));
        }
        _peers = peers;
        _priorityActive = priorityActive;
        _problem = advice?.Kind ?? ConnectionProblemKind.None;
        return result;
    }

    private void Add(List<StatusNotification> result, string kind, DateTimeOffset now, StatusNotification notification)
    {
        if (_lastByKind.TryGetValue(kind, out var last) && now - last < _cooldown) return;
        _lastByKind[kind] = now;
        result.Add(notification);
    }
}
