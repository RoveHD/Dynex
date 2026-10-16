namespace Dynex.Desktop;

public sealed record NetworkIdentity(string Id, string Name);
public sealed record NetworkProfileEntry(string Id, string Name, DateTimeOffset LastUsedUtc, NetworkProfilePolicy Policy);

// Only connection-specific preferences belong here. Games, app rules, autostart and identity stay on this PC.
public sealed record NetworkProfilePolicy
{
    public double CapacityMbps { get; init; } = 200;
    public DateTimeOffset? CapacityMeasuredAtUtc { get; init; }
    public bool AutomaticSpeedTestAttempted { get; init; }
    public double? UploadCapacityMbps { get; init; }
    public double? IdleLatencyMs { get; init; }
    public double? LoadedLatencyMs { get; init; }
    public string Profile { get; init; } = "Standard";
    public double BackgroundPercent { get; init; } = 65;
    public double ReservePerGamerMbps { get; init; } = 10;
    public double? ManualLimitMbps { get; init; }
    public double? SharedDownloadMaximumMbps { get; init; }
    public double SharePriority { get; init; } = 1;
    public bool UploadLimitEnabled { get; init; } = true;
    public double? ManualUploadLimitMbps { get; init; }
    public string RouterTarget { get; init; } = "";
    public string ExternalTarget { get; init; } = "1.1.1.1";
    public bool AutoConnectLan { get; init; } = true;
    public string DirectPeers { get; init; } = "";

    internal static NetworkProfilePolicy From(UserSettings s) => new()
    {
        CapacityMbps = s.CapacityMbps, CapacityMeasuredAtUtc = s.CapacityMeasuredAtUtc,
        AutomaticSpeedTestAttempted = s.AutomaticSpeedTestAttempted, UploadCapacityMbps = s.UploadCapacityMbps,
        IdleLatencyMs = s.IdleLatencyMs, LoadedLatencyMs = s.LoadedLatencyMs, Profile = s.Profile,
        BackgroundPercent = s.BackgroundPercent, ReservePerGamerMbps = s.ReservePerGamerMbps,
        ManualLimitMbps = s.ManualLimitMbps, SharedDownloadMaximumMbps = s.SharedDownloadMaximumMbps, SharePriority = s.SharePriority,
        UploadLimitEnabled = s.UploadLimitEnabled, ManualUploadLimitMbps = s.ManualUploadLimitMbps,
        RouterTarget = s.RouterTarget, ExternalTarget = s.ExternalTarget, AutoConnectLan = s.AutoConnectLan, DirectPeers = s.DirectPeers
    };

    internal UserSettings Apply(UserSettings s) => s with
    {
        CapacityMbps = CapacityMbps, CapacityMeasuredAtUtc = CapacityMeasuredAtUtc,
        AutomaticSpeedTestAttempted = AutomaticSpeedTestAttempted, UploadCapacityMbps = UploadCapacityMbps,
        IdleLatencyMs = IdleLatencyMs, LoadedLatencyMs = LoadedLatencyMs, Profile = Profile,
        BackgroundPercent = BackgroundPercent, ReservePerGamerMbps = ReservePerGamerMbps,
        ManualLimitMbps = ManualLimitMbps, SharedDownloadMaximumMbps = SharedDownloadMaximumMbps, SharePriority = SharePriority,
        UploadLimitEnabled = UploadLimitEnabled, ManualUploadLimitMbps = ManualUploadLimitMbps,
        RouterTarget = RouterTarget, ExternalTarget = ExternalTarget, AutoConnectLan = AutoConnectLan, DirectPeers = DirectPeers
    };
}

internal static class NetworkProfiles
{
    internal const int MaximumProfiles = 64;
    internal static UserSettings Capture(UserSettings settings)
    {
        if (settings.ActiveNetworkProfileId is not { } id || settings.NetworkProfiles is null) return settings;
        var index = Array.FindIndex(settings.NetworkProfiles, p => p?.Id == id);
        if (index < 0) return settings;
        var policy = NetworkProfilePolicy.From(settings);
        if (settings.NetworkProfiles[index].Policy == policy) return settings;
        var profiles = settings.NetworkProfiles.ToArray();
        profiles[index] = profiles[index] with { Policy = policy };
        return settings with { NetworkProfiles = profiles };
    }

    internal static UserSettings Activate(UserSettings settings, NetworkIdentity network, DateTimeOffset now)
    {
        settings = Capture(settings);
        var existing = settings.NetworkProfiles.FirstOrDefault(p => p.Id == network.Id);
        // Migration associates existing settings with the first positively identified Windows network.
        var policy = existing?.Policy ?? (settings.NetworkProfiles.Length == 0 && settings.ActiveNetworkProfileId is null
            ? NetworkProfilePolicy.From(settings) : new NetworkProfilePolicy());
        var entry = new NetworkProfileEntry(network.Id, existing?.Name ?? network.Name, now, policy);
        var profiles = settings.NetworkProfiles.Where(p => p.Id != network.Id).OrderByDescending(p => p.LastUsedUtc)
            .Take(MaximumProfiles - 1).Prepend(entry).ToArray();
        return policy.Apply(settings) with { ActiveNetworkProfileId = network.Id, NetworkProfiles = profiles };
    }

    internal static void Validate(UserSettings settings)
    {
        if (settings.NetworkProfiles is null || settings.NetworkProfiles.Length > MaximumProfiles)
            throw new ArgumentException("Zu viele Netzwerkprofile (höchstens 64).");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in settings.NetworkProfiles)
        {
            if (p is null || !Guid.TryParseExact(p.Id, "D", out var id) || id == Guid.Empty || !ids.Add(p.Id) ||
                string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 128 || p.Name.Any(char.IsControl) || p.Policy is null)
                throw new ArgumentException("Ungültiges Netzwerkprofil.");
            SettingsStore.Validate(p.Policy.Apply(new UserSettings()));
        }
        if (settings.ActiveNetworkProfileId is { } active && !ids.Contains(active))
            throw new ArgumentException("Das aktive Netzwerkprofil fehlt.");
    }
}
