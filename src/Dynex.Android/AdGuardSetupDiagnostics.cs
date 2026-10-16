using Dynex.Mobile;

namespace Dynex.Android;

/// <summary>Setup evidence, never an acknowledgement from the external AdGuard app.</summary>
public static class AdGuardSetupDiagnostics
{
    public static string WaitingStatus(ProxySnapshot? traffic, bool requested) => traffic switch
    {
        { AuthenticationFailures: > 0, AuthenticatedConnections: 0 } => "AdGuard-Proxy-Anmeldung abgewiesen · erneut verbinden",
        { OutboundFailures: > 0 } => "Proxy erreichbar · Weiterleitung ins Netz fehlgeschlagen · AdGuard-App-Ausnahme prüfen",
        { AuthenticatedConnections: > 0 } => "AdGuard am Proxy angemeldet · aktuell keine Datenweiterleitung bestätigt",
        _ => requested ? "AdGuard-Einrichtung angefragt · keine Proxy-Verbindung bestätigt" : "Proxy bereit · AdGuard verbinden"
    };

    public static string? VpnIntegrationHint(bool vpnInstalled, bool forwardingObserved) => vpnInstalled && !forwardingObserved
        ? "AdGuard VPN ist installiert. Prüfe in AdGuard, ob die Integration mit AdGuard VPN aktiv ist: Sie kann den DYNEX-Proxy umgehen. Für den DYNEX-Proxy muss diese Integration aus sein; dadurch entfällt die IP-Verschleierung über AdGuard VPN. Der AdGuard-Werbeblocker bleibt aktiv. DYNEX ändert diese Einstellung nicht."
        : null;
}
