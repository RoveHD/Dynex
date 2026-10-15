namespace Dynex.Core;

/// <summary>What the user sees: an observed cause and exactly one concrete next step.</summary>
public enum ConnectionProblemKind { None, NoPrivateNetwork, FirewallMissing, VersionMismatch, OtherMode, ClockSkew, WrongNetwork, PeerLost, NoResponse, NotConfirmed, ReceiveError, ClientIsolation }

/// <summary>Read-only observations about the local network used to explain why PCs do not see each other.</summary>
public sealed record LanEnvironment(string? WifiName = null, int? OtherUpnpDevices = null)
{
    private static readonly string[] GuestMarkers = ["gast", "guest", "visitor", "besucher", "hotspot"];
    public bool GuestNetwork => WifiName is { } name && GuestMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

public sealed record ConnectionAdvice(ConnectionProblemKind Kind, string Cause, string NextStep)
{
    public bool IsProblem => Kind != ConnectionProblemKind.None;
    public override string ToString() => IsProblem ? $"{Cause} Nächster Schritt: {NextStep}" : Cause;
}

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
    long SelfDatagrams = 0,
    string? LocalVersion = null,
    IReadOnlyList<RemoteVersion>? RemoteVersions = null,
    long UnsupportedProtocolDatagrams = 0,
    string? LostPeerName = null,
    DateTimeOffset? LostPeerAtUtc = null,
    string? ReceiveError = null)
{
    public string Hint => Assess().ToString();

    /// <summary>
    /// Orders causes from "fix on this PC" to "fix on the other PC". The firewall result comes from the
    /// separate read-only Windows check and is optional because the transport layer cannot observe it.
    /// </summary>
    public ConnectionAdvice Assess(bool? firewallConfigured = null, LanEnvironment? lan = null)
    {
        var mismatched = (RemoteVersions ?? []).Where(v => LocalVersion is not null && v.Version != LocalVersion).ToArray();
        if (ReceiveError is not null)
            return new(ConnectionProblemKind.ReceiveError, $"Windows hat den DYNEX-Empfang unterbrochen ({ReceiveError}). DYNEX versucht es automatisch erneut.",
                "Kurz warten. Bleibt der Hinweis länger als eine Minute, DYNEX über das Infobereich-Menü beenden und neu starten.");
        if (ConnectedPeers > 0)
            return mismatched.Length > 0
                ? new(ConnectionProblemKind.VersionMismatch, $"{ConnectedPeers} anderer PC verbunden, aber {Describe(mismatched)} nutzt eine andere DYNEX-Version als dieser PC ({LocalVersion}).",
                    $"DYNEX auf {Names(mismatched)} auf Version {LocalVersion} aktualisieren (Einstellungen → Updates → Jetzt prüfen), damit Limits und Diagnosen übereinstimmen.")
                : new(ConnectionProblemKind.None, $"{ConnectedPeers} anderer PC verbunden; Statuspakete werden empfangen.", "");
        if (LocalAddresses.Count == 0)
            return new(ConnectionProblemKind.NoPrivateNetwork, "Keine freigegebene lokale IPv4-Adresse verfügbar: Dieses Netzwerk ist in Windows nicht als privat eingestuft oder getrennt.",
                "„Windows-Netzwerk öffnen“ wählen, die aktuelle WLAN-/LAN-Verbindung öffnen und das Netzwerkprofil auf „Privat“ stellen. Auf dem anderen PC ebenso.");
        if (firewallConfigured == false)
            return new(ConnectionProblemKind.FirewallMissing, "Die DYNEX-Firewallfreigabe fehlt auf diesem PC. Andere PCs können DYNEX deshalb nicht erreichen.",
                "DYNEX-Setup auf diesem PC erneut ausführen und die Windows-Rückfrage bestätigen.");
        if (UnsupportedProtocolDatagrams > 0 || mismatched.Length > 0)
            return new(ConnectionProblemKind.VersionMismatch,
                mismatched.Length > 0
                    ? $"Ein anderer PC antwortet, nutzt aber eine andere DYNEX-Version ({Describe(mismatched)}; dieser PC: {LocalVersion ?? "unbekannt"})."
                    : "Ein anderer PC sendet ein neueres, hier nicht unterstütztes DYNEX-Verbindungsprotokoll.",
                mismatched.Length > 0 && mismatched.All(v => IsOlder(v.Version, LocalVersion))
                    ? $"DYNEX auf {Names(mismatched)} auf Version {LocalVersion} aktualisieren."
                    : "DYNEX auf diesem PC aktualisieren (Einstellungen → Updates → Jetzt prüfen), danach auf beiden PCs dieselbe Version verwenden.");
        if (OtherModeDatagrams > 0)
            return new(ConnectionProblemKind.OtherMode, "Pakete eines anderen DYNEX-Verbindungsmodus beobachtet (Absender unbestätigt): Ein PC nutzt die automatische Verbindung, der andere eine private Gruppe.",
                "Auf beiden PCs „Automatisch im privaten LAN verbinden“ aktivieren oder auf einem PC einen Gruppencode erzeugen und auf dem anderen importieren.");
        if (AuthenticatedClockSkewDatagrams > 0)
            return new(ConnectionProblemKind.ClockSkew, "Ein authentifiziertes Gruppenpaket wurde wegen mehr als 30 Sekunden Zeitabweichung abgewiesen.",
                "Auf beiden PCs Windows-Einstellungen → Zeit und Sprache → „Jetzt synchronisieren“ wählen.");
        if (ScopeRejectedDatagrams > 0)
            return new(ConnectionProblemKind.WrongNetwork, "UDP-Pakete außerhalb der freigegebenen privaten LAN-Schnittstelle beobachtet. Die PCs sind vermutlich in verschiedenen Netzen (z. B. Gast-WLAN oder zweiter Router).",
                "Beide PCs mit demselben Heimnetz verbinden und dort auf „Privat“ stellen.");
        if (LostPeerName is not null)
            return new(ConnectionProblemKind.PeerLost, $"„{LostPeerName}“ antwortet seit {LostPeerAtUtc?.ToLocalTime():HH:mm} Uhr nicht mehr (Standby, Netzwechsel oder DYNEX beendet).",
                $"Auf „{LostPeerName}“ prüfen, ob der PC wach ist und DYNEX läuft. Nach Standby oder WLAN-Wechsel verbindet DYNEX automatisch neu, sobald beide PCs im selben privaten Netz sind.");
        if (ReceivedDatagrams - SelfDatagrams <= 0 && lan?.GuestNetwork == true)
            return new(ConnectionProblemKind.ClientIsolation, $"Dieser PC ist mit dem Gastnetz „{lan.WifiName}“ verbunden. Gastnetze trennen Geräte voneinander, deshalb sehen sich die PCs nicht.",
                "Beide PCs mit dem normalen Heim-WLAN bzw. per LAN-Kabel verbinden.");
        if (ReceivedDatagrams - SelfDatagrams <= 0 && lan?.OtherUpnpDevices == 0)
            return new(ConnectionProblemKind.ClientIsolation, "Außer dem Router antwortet kein anderes Gerät im Netz. Vermutlich ist im Router „Client-/AP-Isolation“ (WLAN-Geräte voneinander abschirmen) aktiv.",
                "Im Router die Einstellung „Geräte untereinander kommunizieren lassen“ einschalten bzw. „AP-/Client-Isolation“ ausschalten, oder die PCs per LAN-Kabel verbinden.");
        if (ReceivedDatagrams - SelfDatagrams <= 0)
            return new(ConnectionProblemKind.NoResponse, "Der andere PC antwortet nicht: Noch keine UDP-Pakete anderer PCs beobachtet. Die Ursache ist noch nicht bestimmt (DYNEX aus, Firewall, Gastnetz oder Geräteisolation).",
                "Auf dem anderen PC DYNEX starten. Hilft das nicht, dort einen Gruppencode erzeugen und hier importieren; er enthält die direkte Adresse. Im Router „Gastnetz“ bzw. „AP-/Client-Isolation“ abschalten.");
        if (AcceptedStatusDatagrams == 0 && AcceptedHandshakes == 0)
            return new(ConnectionProblemKind.NotConfirmed, "UDP-Pakete kommen an, bisher konnte kein DYNEX-Status bestätigt werden. Verbindungsmodus, Gruppencode oder Uhrzeit weichen ab.",
                "Auf beiden PCs denselben Verbindungsmodus wählen; bei privater Gruppe den Code neu importieren und die Windows-Uhrzeit synchronisieren.");
        return new(ConnectionProblemKind.NotConfirmed, "DYNEX-Pakete wurden bestätigt, aber aktuell ist kein anderer PC verbunden.",
            "Auf dem zweiten PC prüfen, ob DYNEX läuft und mit demselben Netzwerk verbunden ist.");
    }

    private static string Names(IEnumerable<RemoteVersion> versions) =>
        string.Join(", ", versions.Select(v => $"„{v.DeviceName ?? "anderer PC"}“").Distinct());
    private static string Describe(IEnumerable<RemoteVersion> versions) =>
        string.Join(", ", versions.Select(v => $"„{v.DeviceName ?? "anderer PC"}“ {v.Version}").Distinct());
    internal static bool IsOlder(string remote, string? local) =>
        local is not null && (!Version.TryParse(remote, out var r) || Version.TryParse(local, out var l) && r < l);
}

/// <summary>Version from a signed AutoLAN handshake. Releases before 1.0.6 do not announce one.</summary>
public sealed record RemoteVersion(Guid DeviceId, string? DeviceName, string Version)
{
    public const string BeforeVersionAnnouncement = "vor 1.0.6";
}

public static class LanDiscovery
{
    /// <summary>Only active IPv4 addresses on explicitly private Windows network profiles.</summary>
    public static IReadOnlyList<string> GetPrivateLocalAddresses() => AutoLanNetworkScope.Read().Bindings
        .Select(b => b.Address.ToString()).Distinct(StringComparer.Ordinal).Take(16).ToArray();
}
