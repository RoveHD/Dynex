namespace Dynex.Android;

/// <summary>Immutable, display-ready status published by the mobile runtime.</summary>
public sealed record MobileSnapshot(
    string ConnectionStatusText,
    bool IsConnected,
    bool IsObservingGaming,
    bool GamingReported,
    bool IsPaused,
    bool LimitPlanned,
    bool LimitApplied,
    double? DownloadLimitMbps,
    double? DownloadMbps,
    double? UploadMbps,
    IReadOnlyList<MobileDeviceSnapshot> Devices,
    bool IsOnWifi = false,
    bool OtherVpnActive = false,
    bool ProxyMode = true,
    bool IsRunning = false,
    bool AdGuardSetupRequested = false,
    bool ProxyForwardingObserved = false,
    bool HasNetworkTransport = false)
{
    public static MobileSnapshot Initial { get; } = new(
        "", false, false, false, false,
        false, false, null, null, null, Array.Empty<MobileDeviceSnapshot>(), false, false, true, false);
}

public sealed record MobileDeviceSnapshot(
    string Name,
    string StatusText,
    double? DownloadMbps,
    double? UploadMbps,
    double? LimitMbps);
