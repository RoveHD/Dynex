# Geräte, Programmregeln, Netzwerkprofile und Verbrauch

## Einstieg

- **Verbundene PCs → Geräte verwalten**: authentifizierte PCs bleiben offline in der Liste. Ein lokaler Alias ändert nur die Darstellung auf diesem PC. Ignorierte und gesperrte Identitäten nehmen nicht an Empfang, Kopplung oder gemeinsamer Budgetberechnung teil; andere PCs werden weiterhin automatisch erkannt. [Protokoll und Grenzen](peer-devices.md).
- **Verbundene PCs → Zwei-PC-Test** (auch in Netzwerk): gezielte, frische Challenge und Gegenprobe für das ausgewählte bekannte Gerät, mit Version und lokaler Firewall-/Netzdiagnose. Gespeicherte Offline-Geräte können geprüft werden. Ohne Antwort bleibt die Ursache unbekannt; Ping allein bestätigt keine DYNEX-Verbindung. Beide PCs benötigen die neue Testfunktion.
- **Einstellungen → Analyse → Programmregeln**: Programm auswählen oder Namen eingeben, Download-/Uploadlimit setzen, hinzufügen und ausdrücklich speichern. Alle Prozesse desselben Namens teilen ein Budget; eine leere Richtung bleibt frei. Die Regeln gelten auch ohne Spiel und wirken zusätzlich zu einem globalen Budget. Explizite Regeln gelten auch für sonst geschützte Spiele/Sprachprogramme. Pause, freie Nachtzeit, ausgeschalteter Treiber und Speedtest setzen die Regeln aus. Lokaler LAN- und Steuerverkehr bleibt frei. Bestätigter Regelstatus kommt vom Filterhelfer, nicht allein aus der gespeicherten Auswahl.
- **Einstellungen → Netzwerk → Netzwerkprofile**: Windows-Netzwerke werden anhand ihrer Netzwerk-ID der tatsächlich gerouteten Schnittstelle unterschieden. Messwerte, Downloadprofil, Gewichtung, Uploadlimits, Messziele und Verbindungseinstellungen werden pro Netzwerk gespeichert. Spiele, Programmregeln und Autostart bleiben PC-weit. Neue Netzwerke starten ohne fremde Messwerte oder direkte Geräteadressen; vorhandene Einstellungen werden beim ersten erkannten Netzwerk übernommen. Die Netzwerke können lokal benannt werden. Bei Netzwerkwechsel werden veraltete Eingaben abgewiesen; „Neu laden“ lädt die aktuellen Werte.
- **Verbundene PCs oder Analyse → Verbrauchsverlauf**: Tag oder sieben Tage auswählen. Download und Upload sind getrennte Schätzungen aus gemessenen Mbps. Offlinezeiten werden nicht hochgerechnet; fehlende Richtungen und Zeiträume bleiben sichtbar. Lokale Aliase gelten auch für neue Verlaufsdaten.

## Datenspeicherung und Grenzen

`settings.json` enthält Netzprofile und Programmregeln atomisch. Portable Einstellungssicherungen enthalten die Programmregeln, aber keine Netz-IDs, alten Netzprofile oder deren Speedtestwerte. `devices.json` speichert Geräte und Ausschlüsse; eine beschädigte Datei sperrt den Netzwerkdienst sichtbar, ohne die Datei zu überschreiben oder die App am Start zu hindern. Nach Wiederherstellung der Datei DYNEX neu starten.

`usage-history.json` enthält maximal 30.000 Zeitblöcke von je 15 Minuten und bis zu 90 Tage Verlauf. Die globale Speichergrenze kann ältere Daten früher entfernen; die Anzeige nennt dies. Es wird spätestens alle fünf Minuten sowie beim geordneten Stop gespeichert. Ein harter Absturz kann die letzten fünf Minuten verlieren. Beschädigte Verlaufsdateien werden nicht überschrieben. Vor dem ersten Messwert existiert keine Vollerfassung, und Messlücken sind kein gemessener Nullverbrauch.

Die Netzwerk-ID stammt aus der [Windows Network List Manager API](https://learn.microsoft.com/windows/win32/api/netlistmgr/nf-netlistmgr-inetwork-getnetworkid). Eine nicht eindeutig erkannte Verbindung wird ausdrücklich als unbekannt gemeldet. Die Funktion setzt keine Windows-Netzwerkkategorie um und übergeht keine Router- oder Firewall-Isolation.

Der Downloadfilter arbeitet am PC, nachdem eingehende Daten den Anschluss passiert haben. Programmzuordnung benötigt eine frische, eindeutige Socket-/Prozessidentität. VPNs, Proxys, unbekannte Besitzer und kurze Bursts können daher nicht als exakt kontrollierter Internetverbrauch ausgegeben werden.

## Isolierte Prüfungen

`Dynex.Devices.Tests` und `Dynex.DeviceRuntime.Tests` prüfen authentifizierte Probes, Offlineziele, Abbruch, Version, Paketverlust, Ausschlüsse und Persistenz. `Dynex.ApplicationRules.Tests` prüft Regeln, Socketidentität, getrennte Richtungen und den tatsächlichen Helper-Befehlsablauf mit simuliertem Paketgerät. `Dynex.UsageHistory.Tests` prüft Tagesgrenzen/DST, Lücken, unbekannte Werte, laufende Zeitblöcke und Persistenz. `Dynex.Runtime.Tests -- --profiles-only` prüft Netzwerkwechsel und Messwertzuordnung. Die UI-Prüfung verwendet einen unsichtbaren separaten Desktop und isolierte Daten.

Diese Prüfungen ersetzen weder den Test zwischen zwei physischen PCs noch die Durchsatzmessung mit dem echten Filtertreiber.
