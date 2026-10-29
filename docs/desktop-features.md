# Oberfläche und Messwerte

Die Hauptnavigation besteht aus **Übersicht**, **Verbundene Geräte** und **Analyse**. Unter **Einstellungen** bleiben **Allgemein**, **Darstellung** und **Updates**.

- **Übersicht:** lokale Paketpriorisierung, aktuelle Download-/Uploadwerte, Router-/Internetping und Gerätewerte. Messwerte sind von erkannten Spiel-/Sprach-/Videopaketen getrennt.
- **Verbundene Geräte:** kompakte Tabelle mit Name, Verbindung, Status und beiden Datenraten. Seitenwechsel erscheinen nur bei mehreren Seiten. **Geräte verwalten** erhält Aliasnamen und Ausschlüsse; **Verbrauchsverlauf** zeigt Tages- und Sieben-Tage-Ansichten mit Messlücken.
- **Gerätesuche aktualisieren:** fordert die automatische Erkennung an und aktualisiert die Anzeige. Währenddessen ist der Button gesperrt; Abbruch und Fehler werden angezeigt. Die lokale Priorisierung benötigt keine Verbindung zu einem zweiten PC.
- **Analyse:** Spielzeit, gemessene Verbindungsqualität, Störungen und Routerhinweise.
- **Allgemein:** ein Schalter für Paketpriorisierung, Windows-Start, Benachrichtigungen, Ping-Alarm, eine manuelle Speedtest-Diagnose und Sichern/Wiederherstellen.

## Automatischer Betrieb

Erkannte laufende Spiele werden unabhängig vom Fokus auf tatsächlichen zugeordneten Paketverkehr geprüft. Prozessname allein, ein Menü oder ein offener Socket sind kein Gaming-Nachweis. Die alte Fokus-/On-/Off-Auswahl wird auf Automatik migriert. Der globale Priorisierungsschalter bleibt wirksam.

Die separaten Spiele-/Netzwerkseiten, manuelle Gruppen-/IP-Eingaben, Prozesslisten und der Zwei-PC-Test stehen nicht mehr in der normalen Oberfläche. Bereits gespeicherte Spiele-Zusätze, Pingziele, Netzwerkprofile und Kopplungen bleiben erhalten. Eine bestehende private Gruppe wird nicht stillschweigend auf LAN-Erkennung umgestellt. Dafür erscheint in der Geräteansicht der ausdrückliche Button **Automatische Gerätesuche aktivieren**; der bisherige Gruppenschlüssel bleibt gespeichert.

## Daten und Grenzen

`settings.json`, `devices.json` und `usage-history.json` bleiben erhalten. Alte Limitfelder können weiterhin keine Begrenzung aktivieren. DYNEX konfiguriert weder Router-QoS noch SQM und garantiert keine bestimmte Latenz.

Unbekannte Messwerte erscheinen als **—**, gemessener Leerlauf als **0,0 Mbps**. Adapterverkehr kann Heimnetzverkehr enthalten. Der Speedtest startet nur auf Anforderung und steuert keine Priorisierung.
