namespace Dynex.Core;

/// <summary>Transport observations only. Received datagrams do not establish peer identity or trust.</summary>
public sealed record PeerNetworkDiagnostics(
    string Mode,
    IReadOnlyList<string> LocalAddresses,
    IReadOnlyList<string> DirectPeers,
    long SentDatagrams = 0,
    long ReceivedDatagrams = 0,
    long AcceptedStatusDatagrams = 0,
    long AcceptedHandshakes = 0,
    long InvalidOrRejectedDatagrams = 0,
    long ScopeRejectedDatagrams = 0,
    long OtherModeDatagrams = 0,
    DateTimeOffset? LastReceiveUtc = null,
    int ConnectedPeers = 0,
    int LearnedEndpoints = 0,
    long AuthenticatedClockSkewDatagrams = 0,
    long SelfDatagrams = 0)
{
    public string Hint => ConnectedPeers > 0
        ? $"{ConnectedPeers} anderer PC verbunden; Statuspakete werden empfangen."
        : LocalAddresses.Count == 0
            ? "Keine freigegebene lokale IPv4-Adresse verfügbar. Privates Windows-Netzwerk und Netzwerkstatus prüfen."
            : OtherModeDatagrams > 0
                ? "Pakete eines anderen DYNEX-Verbindungsmodus beobachtet (Absender unbestätigt). Auf beiden PCs automatisch verbinden aktivieren oder auf einem PC einen Gruppencode erzeugen und auf dem anderen importieren."
                : AuthenticatedClockSkewDatagrams > 0
                    ? "Ein authentifiziertes Gruppenpaket wurde wegen mehr als 30 Sekunden Zeitabweichung abgewiesen. Windows-Uhrzeit auf beiden PCs synchronisieren."
                    : ScopeRejectedDatagrams > 0
                        ? "UDP-Pakete außerhalb der freigegebenen privaten LAN-Schnittstelle beobachtet. Beide PCs müssen im selben privaten Netz erreichbar sein."
                        : ReceivedDatagrams - SelfDatagrams <= 0
                            ? "Noch keine UDP-Pakete anderer PCs beobachtet. Firewall, Gastnetz oder Geräteisolation sind mögliche Ursachen; die Ursache ist noch nicht bestimmt."
                            : AcceptedStatusDatagrams == 0 && AcceptedHandshakes == 0
                                ? "UDP-Pakete kommen an, bisher konnte kein DYNEX-Status bestätigt werden. Verbindungsmodus und Gruppencode auf beiden PCs prüfen; auch eine abweichende Uhrzeit kann Pakete ungültig machen."
                                : "DYNEX-Pakete wurden bestätigt, aber aktuell ist kein anderer PC verbunden. Verbindung und DYNEX auf dem zweiten PC prüfen.";
}

public static class LanDiscovery
{
    /// <summary>Only active IPv4 addresses on explicitly private Windows network profiles.</summary>
    public static IReadOnlyList<string> GetPrivateLocalAddresses() => AutoLanNetworkScope.Read().Bindings
        .Select(b => b.Address.ToString()).Distinct(StringComparer.Ordinal).Take(16).ToArray();
}
