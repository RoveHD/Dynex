# DYNEX: Architektur der reinen Priorisierung

`Dynex.Core` stellt LAN-Kommunikation, Peer-Vertrauen, Verbindungsdiagnosen, Ping-/Sitzungsauswertung und gemeinsame Datenmodelle bereit. `Dynex.Desktop` verbindet WPF-Oberfläche, Spiel-/Prozesserkennung, Netzwerküberwachung, Geräteverwaltung und persistente Daten. `Dynex.Shaper` ist der getrennte erhöhte Priorisierungs-Helfer; sein historischer Projektname bleibt aus Kompatibilitätsgründen bestehen.

## Datenfluss

1. Der Desktop erkennt Spielprozesse mit Prozess-ID und Startzeit und bestimmt die für Beobachtung geeigneten Spiele.
2. Der Helfer ordnet tatsächliche Netzwerkpakete anhand frischer Socket-/Prozesstabellen zu.
3. Der eingeschaltete Priorisierungsschalter aktiviert die Paketverarbeitung auch ohne Spiel; unbekannte Zuordnung bleibt unverändert.
4. Geeignete ausgehende Pakete erhalten CS4 für Spiele, EF für Sprache oder AF41 für nachgewiesene Videoverbindungen. Alle Pakete werden sofort weitergeleitet.
5. Bestätigungen und Zähler gelangen über die prozessgebundene lokale Pipe zur Oberfläche; Durchsatz- und Gamingmeldungen werden authentifiziert im privaten LAN geteilt.

Es gibt keine ausgeführte Budgetplanung, keine adaptive Bandbreitenabsenkung und keine individuellen Programm- oder Gerätelimits. Historische Modellfelder und reine Berechnungsklassen dienen der Lese-/Quellkompatibilität; Runtime, IPC und Paketpfad können daraus keine Limits aktivieren.

## LAN und Sicherheit

AutoLAN verwendet pro Gerät ECDSA-Identität, ECDH-Schlüsselaustausch und AES-GCM. Replay-/Frische- und Netzwerkbereichsprüfungen bleiben bestehen. Eine neue Identität im Windows-Privatnetz wird beim ersten Kontakt automatisch vertraut; optional gibt es private gemeinsame Gruppen. Die gezielte Firewallfreigabe betrifft UDP 39274 im privaten lokalen Subnetz.

Der Helfer verwendet den separat installierten Windows Packet Filter (NDISRD), keinen eigenen Kerneltreiber. Benutzersitzung und Prozessidentitäten begrenzen die Pipe. Watchdog, Shutdown-ACK, Freigabefehler und begrenzte Wiederherstellung bleiben auch ohne Limits notwendig.

Ein einmalig eingerichteter Windows-Dienst startet den Helfer aus einem administrativ geschützten, über Dateihashes geprüften Paket. Die Desktop-App startet keinen Prozess mehr mit `runas`. Der lokale Dienst authentifiziert SID, Prozess-ID, Startzeit und registrierten Anwendungspfad; ausführbarer Pfad und Argumentstruktur des Helfers sind fest. Die Sitzungspipe erlaubt nur dem ursprünglichen Benutzer und SYSTEM Zugriff und weist Netzwerkidentitäten ab. Der SYSTEM-Helfer verwendet für ELFIX-Hinweise ausdrücklich das Profil und die Identität des ursprünglichen Desktop-Benutzers. Desktop-Ende, Heartbeat-Ausfall, Netzwechsel und Dienstende lösen die geprüfte Freigabe aus.

## Plattformen

Android verbindet sich im WLAN weiter mit PCs, steuert aber keine Bandbreite und startet dafür keinen VPN-Tunnel. Ein schon in AdGuard konfigurierter lokaler SOCKS-Proxy bleibt unbegrenzt erreichbar. Ohne passenden Messpfad meldet Android unbekannten Verbrauch.

## Systemgrenze

DYNEX programmiert keine Routerwarteschlangen und garantiert keinen niedrigen Ping. Routerseitiges SQM/QoS und passende Gesamtbandbreite regeln die gemeinsame Leitung. DSCP-Markierungen helfen dort nur bei entsprechender Konfiguration. Weitere Details: [Priorisierung](qos.md), [Android](android.md), [Installation](installation.md).
