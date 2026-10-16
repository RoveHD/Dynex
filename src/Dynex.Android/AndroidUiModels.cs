namespace Dynex.Android;

public enum AppVpnRoute { Unknown, Vpn, Physical }

public static class AdGuardWizardGate
{
    public static bool CanContinue(AppVpnRoute route) => route == AppVpnRoute.Physical;
    public static bool CanFinish(AppVpnRoute route, bool forwardingObserved) => CanContinue(route) && forwardingObserved;
    public static string RouteStatus(AppVpnRoute route) => route switch
    {
        AppVpnRoute.Vpn => "DYNEX wird noch durch ein VPN geleitet. In AdGuard → App-Verwaltung → DYNEX → „Datenverkehr durch AdGuard leiten“ ausschalten. Danach zu DYNEX zurückkehren.",
        AppVpnRoute.Physical => "Geprüft: DYNEX verwendet derzeit eine direkte Netzverbindung außerhalb des VPN. Die Einrichtung ist erst nach beobachteter Weiterleitung fertig.",
        _ => "DYNEX kann die eigene Netzroute derzeit nicht prüfen. Netzverbindung herstellen und anschließend zu DYNEX zurückkehren. Die Einrichtung bleibt offen."
    };
}

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
    bool HasNetworkTransport = false,
    double? SharedCapacityMbps = null,
    double? SharedUploadCapacityMbps = null,
    bool AdGuardVpnInstalled = false)
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
