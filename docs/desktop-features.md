# Geräte, Netzwerkprofile und Verbrauch

## Ansichten

- **Verbundene Geräte → Geräte verwalten:** Geräte bleiben offline gespeichert. Aliasnamen und Ausschlüsse bleiben erhalten. Es gibt keine Limitspalte oder manuelle Gerätebegrenzung.
- **Zwei-Geräte-Test:** authentifizierte Challenge und Gegenprobe, Version und lokale Firewall-/Netzdiagnose. Ping allein bestätigt keine DYNEX-Verbindung.
- **Netzwerkprofile:** Netzwerkidentität, Messwerte, Pingziele und Verbindungseinstellungen bleiben anschlussbezogen gespeichert. Alte Download-/Upload-/Profil-/Gewichtungsfelder werden auf inaktiv migriert.
- **Verbrauchsverlauf:** Tages- und Sieben-Tage-Ansichten mit getrennten Richtungen, sichtbaren Messlücken und lokalen Aliasnamen bleiben erhalten.
- **Analyse und Ping-Verlauf:** Spielzeit, gemessene Verbindungsqualität und Störungen bleiben sichtbar. Neue Auswertungen behaupten keine begrenzte Downloadzeit.

Die Übersicht zeigt bestätigte Paketpriorisierung mit Spiel-/Sprachpaketzählern statt eines Downloadbudgets. Manuelle Geschwindigkeitsmessung bleibt Diagnose; es gibt keinen automatischen Start-Speedtest und keine Abhängigkeit der Priorisierung von einem Messergebnis.

## Entfernte Einstellungen

Automatische/adaptive Budgets, Uploadlimits, gemeinsame Downloadprofile, manuelle Programmregeln, Anteilsgewichtung, Limit-Zeitpläne und Downloadpausen im Tray sind entfernt. **Auch ab Spielstart priorisieren** bleibt erhalten: Das ist eine Regel für Spielbeobachtung unabhängig vom Fokus und benötigt bestätigten Netzwerkverkehr.

## Datenspeicherung

`settings.json`, `devices.json` und `usage-history.json` bleiben erhalten. Alte Limitfelder werden beim Laden, Importieren und Wiederherstellen von Netzwerkprofilen neutralisiert. Kopplungen, Geräte und Verbrauchshistorie werden nicht gelöscht.

Unbekannte Messwerte erscheinen als **—**. Ein gemessener Stillstand ist **0,0 Mbps**. Der am PC gemessene Adapterverkehr kann auch Heimnetzverkehr enthalten. Ohne verfügbare Messung werden keine vollständigen Telemetriedaten behauptet.
