namespace Dynex.Desktop;

public enum GamingMode { Auto, On, Off }
public enum SpeedTestWaitReason { None, Monitoring, Network, Discovery, OpenGame }

public sealed record UserSettings
{
    public string DeviceName { get; init; } = Environment.MachineName;
    public double CapacityMbps { get; init; } = 200;
    public DateTimeOffset? CapacityMeasuredAtUtc { get; init; }
    public double? IdleLatencyMs { get; init; }
    public double? LoadedLatencyMs { get; init; }
    public bool AutomaticSpeedTestAttempted { get; init; }
    public double BackgroundPercent { get; init; } = 65;
    public double ReservePerGamerMbps { get; init; } = 10;
    public double? ManualLimitMbps { get; init; }
    public bool Adaptive { get; init; } = true;
    public bool DriverEnabled { get; init; } = true;
    public bool AutoStart { get; init; } = true;
    public bool AutoUpdate { get; init; }
    public bool BetaUpdates { get; init; }
    public bool AutoConnectLan { get; init; } = true;
    public bool Notifications { get; init; } = true;
    public double SharePriority { get; init; } = 1;
    public string Profile { get; init; } = "Standard";
    public bool SetupGuideDone { get; init; }
    public bool MarkGamePackets { get; init; }
    public GamingMode Mode { get; init; } = GamingMode.Auto;
    public string RouterTarget { get; init; } = "";
    public string ExternalTarget { get; init; } = "1.1.1.1";
    public string GameProcesses { get; init; } = "";
    public string DirectPeers { get; init; } = "";
}

public sealed record PingView(double? Current, double? Jitter, double? Loss, int Count, string Target);
public sealed record DeviceView(string Name, string Address, bool Gaming, string Game, double? LimitMbps, string State,
    double? DownloadMbps = null, double? UploadMbps = null);
public sealed record HistoryPoint(DateTimeOffset Time, double? RouterMs, double? ExternalMs, double? DownloadMbps, bool Protection);
public sealed record RuntimeSnapshot(
    string State, string Detail, bool IsGaming, string Game, bool DriverActive,
    string DriverState, double? LimitMbps, double? DownloadMbps, double? UploadMbps,
    PingView Router, PingView External, IReadOnlyList<DeviceView> Devices,
    IReadOnlyList<HistoryPoint> History, string NetworkStatus, string PairingFingerprint,
    bool DriverAvailable = false, double? SharedDownloadBudgetMbps = null,
    int ParticipantCount = 1, int GamingCount = 0, double? SharedCapacityMbps = null,
    bool SharedBudgetComplete = false, int ConfirmedParticipantCount = 0,
    bool QosActive = false, long GamePriorityPackets = 0, long VoicePriorityPackets = 0,
    string? NetworkProblem = null, bool ProtectionIdle = false,
    Dynex.Core.PeerNetworkDiagnostics? NetworkDiagnostics = null,
    string LimitReason = "", Dynex.Core.ConnectionAdvice? Connection = null, DateTimeOffset? PausedUntil = null,
    Dynex.Core.SessionSummary? LastSession = null, Dynex.Core.RouterTrafficSample? HomeTraffic = null);
