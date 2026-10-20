# DYNEX
### Dynamic Network Experience

DYNEX ist eine native Windows-App für Spiel- und Sprachpaketpriorisierung im privaten Heimnetz. Geräteübersicht, aktuelle Download-/Uploadwerte, Verbrauchsverlauf, Ping-Verlauf und Verbindungsdiagnose bleiben erhalten.

## Neu in 1.0.16

Reine Priorisierung statt Bandbreitenbegrenzung auf Windows und Android. Alte Limits werden neutralisiert; Spielpakete erhalten jetzt tatsächlich CS4-Markierungen für einen passend eingerichteten Router. Geräte-, Verbrauchs- und Pingansichten bleiben erhalten.

## Reine Priorisierung

- Erkannte ausgehende interaktive Spielpakete erhalten **DSCP CS4 (32)**, geeignete Sprachpakete **EF (46)**. Nur IP-Prioritätsfelder und die erforderliche IPv4-Headerprüfsumme ändern sich. Transportheader, Paketinhalt und ECN bleiben erhalten.
- **DYNEX begrenzt keine Downloads oder Uploads.** Es gibt keine gemeinsamen Budgets, adaptiven Kürzungen, manuellen Programm- oder Gerätelimits, Zeitpläne für Limits oder Downloadpausen.
- Der Helfer leitet alle Pakete unmittelbar weiter. Alte Limitparameter können keine Warteschlangen oder Drosselung aktivieren.
- Der Router muss DSCP auswerten, damit Markierungen dort Vorrang bewirken. DYNEX richtet den Router nicht ein und garantiert keinen bestimmten Ping. SQM/CAKE oder andere Bandbreitenregelung gehört auf den Router.
- Priorisierung benötigt keine Geschwindigkeitsmessung. Ein Speedtest wird ausschließlich manuell als Diagnose gestartet.

## Start und bestehende Installationen

Installiere DYNEX auf jedem Windows-PC, auf dem du lokale Spielpakete markieren möchtest. Im privaten LAN werden andere DYNEX-Geräte automatisch erkannt; optional kann eine private gemeinsame Gruppe verwendet werden. Der signierte Windows Packet Filter und die gezielte private LAN-Firewallfreigabe bleiben für Paketverarbeitung und Geräteverbindung erforderlich. Die Einrichtung kann eine Windows-Bestätigung verlangen; die normale Oberfläche läuft ohne Erhöhung.

Bestehende Einstellungen, importierte Sicherungen und gespeicherte Netzwerkprofile werden auf den Betrieb ohne Limits migriert. Geräte, Aliasnamen, Kopplungen, Spiele und Messhistorien bleiben erhalten. Ältere DYNEX-Versionen auf anderen Geräten können ihre eigenen Limits weiterhin anwenden; aktualisiere alle beteiligten Geräte.

Ein geeignetes Spiel muss tatsächlichen Netzwerkverkehr haben. Ohne passenden Verkehr läuft höchstens die passive Beobachtung; ohne Spielbedarf wird die Capture-Sitzung freigegeben. Über **Auch ab Spielstart priorisieren** lassen sich Spiele unabhängig vom Fokus beobachten. Unbekannte oder mehrdeutige Prozesszuordnung führt nicht zu einer Markierung. [Spielerkennung](docs/game-detection.md), [Priorisierung](docs/qos.md).

## Android

Die Android-App bleibt eine energiesparende WLAN-Begleit-App mit Geräteverbindung. Sie erkennt keine Handyspiele und behauptet keine eigene Spielpaketpriorisierung. Es werden weder Handy-Downloads begrenzt noch neue VPN-Tunnel für Limits gestartet.

Ein bereits in AdGuard eingetragener DYNEX-Proxy bleibt als **unbegrenzte Durchleitung** verfügbar, damit ein Update die Internetverbindung nicht unterbricht. Neue Installationen benötigen keinen AdGuard-Assistenten. Ohne Messpfad bleibt der Handyverbrauch ausdrücklich unbekannt. [Android-Verhalten](docs/android.md).

## Ansichten und Daten

Die Übersicht zeigt bestätigte lokale Priorisierung, Spiel-/Sprachpaketzähler, aktuelle Datenraten und Router-/Internetping. Geräteansicht, Aliasverwaltung, Zwei-Geräte-Test, Analyse, Netzwerkprofile und Verbrauchsverlauf bleiben erhalten. Unbekannte Messwerte erscheinen als **—**, gemessener Leerlauf als **0,0 Mbps**. Netzwerkprofile speichern Messwerte und Verbindungseinstellungen; alte Limitfelder sind wirkungslos. [Desktop-Funktionen](docs/desktop-features.md).

Optionale Hintergrundupdates, Einstellungen sichern/wiederherstellen und Windows-Autostart bleiben verfügbar. Der Updater lädt sein eigenes Installationspaket weiterhin schonend herunter; daraus entsteht kein Limit für andere Programme. [Updates](docs/updates.md).

## Entwicklung und Prüfung

Die Projekte liegen unter `src/`, die isolierten ausführbaren Tests unter `tests/`. Die Windows-Pakettests prüfen Markierungen für IPv4/IPv6, Nutzdatenintegrität, unmittelbare Weiterleitung trotz alter Limits, Prozesszuordnung, ACKs, Watchdog und Freigabe. Runtime-Prüfungen kontrollieren Migration und den Betrieb ohne automatische Speedtests. Ein Test mit simuliertem Paketgerät ersetzt keinen Test mit echtem Filtertreiber, Router und zwei physischen PCs.

[Architektur](docs/architecture.md) · [Paketverarbeitung](docs/shaping.md) · [Installation](docs/installation.md) · [Anti-Cheat-Grenzen](docs/anticheat.md)
