# DYNEX 1.0.17

- Allgemeine Paketpriorisierung bleibt bei eingeschaltetem Schalter auch ohne laufendes Spiel aktiv. Abschalten, manuelle Diagnose und Beenden geben die Sitzung weiterhin bestätigt frei; nach der Diagnose wird sie wieder aufgenommen.
- Nachgewiesene Videoverbindungen erhalten AF41 unterhalb von Gaming CS4 und Sprache EF. Browser und ELFIX werden anhand konkreter Verbindungen behandelt; der Programmname allein priorisiert keinen kompletten Browser oder ELFIX-Download.
- Passive TLS-/QUIC-v1/v2-ClientHello-Erkennung für Medien-CDNs von YouTube und Netflix sowie tatsächliche sichtbare HTTP-Medientypen. ECH, unbekannte verschlüsselte ELFIX-Hoster und fehlende Handshakes bleiben ohne zusätzliche Klassifizierung.
- Eigener bestätigter Video-Paketzähler vom Helfer bis zur Oberfläche. ECN, Transportheader und Nutzdaten bleiben erhalten; DYNEX führt weiterhin keine Bandbreitenbegrenzung aus.

Grenzen und Details: [QoS](qos.md). Die Routerkonfiguration ist gesondert vorzunehmen; bei CAKE DiffServ4 gehört AF41 zur Video-Klasse. Die universelle Erkennung beliebiger verschlüsselter Videos oder die Unterscheidung eines verschlüsselten Medien-Downloads von Wiedergabe ist ohne Anwendungssignal nicht zugesichert.
