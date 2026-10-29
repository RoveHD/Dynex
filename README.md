# DYNEX
### Dynamic Network Experience

DYNEX ist eine native Windows-App für Spiel-, Sprach- und Videopaketpriorisierung im privaten Heimnetz. Geräteübersicht, aktuelle Download-/Uploadwerte, Verbrauchsverlauf, Ping-Verlauf und Verbindungsdiagnose bleiben erhalten.

## Neu in 1.0.27

Behebt „Zugriff verweigert“ beim Beenden des geschützten Helfers vor einem Update. Der Desktop benötigt dafür keine zusätzlichen Prozessrechte; Freigabe und Prozessende werden weiterhin geprüft. Bei bereits blockiertem Update das Setup einmal direkt starten. [Änderungen](docs/release-notes-1.0.27.md).

## Neu in 1.0.26

Die Oberfläche konzentriert sich auf automatische Paketpriorisierung, Geräte und Messwerte. In den Einstellungen bleiben Allgemein, Darstellung und Updates. Erkannte laufende Spiele werden auch im Hintergrund auf echten Spielverkehr geprüft; Fokus- und Prozessnamen-Felder entfallen. Geräteübersicht, Verbrauch und Ping bleiben erhalten. [Änderungen](docs/release-notes-1.0.26.md).

## Neu in 1.0.24

Upgrades ersetzen das Helferpaket jetzt vollständig, statt neue Dateien über alte zu kopieren. Damit führen übrig gebliebene Runtime-DLLs früherer Versionen nicht mehr zum Starttest-Fehler 22 und zur Rückkehr auf 1.0.21. Bei einem fehlgeschlagenen Update wird der gesamte bisherige Helferordner wiederhergestellt. [Änderungen](docs/release-notes-1.0.24.md).

## Neu in 1.0.22

Die Windows-Priorisierung startet ihren Netzwerkhelfer jetzt über einen einmalig eingerichteten, geschützten Dienst. Normale Starts und Verbindungswechsel lösen keine wiederholten Administratorabfragen mehr aus. Die Einrichtung und Änderungen am Dienstpaket verlangen weiterhin eine Windows-Bestätigung. Fehlende oder veränderte Helferdateien werden vor der Einrichtung erkannt. [Änderungen](docs/release-notes-1.0.22.md), [Installation](docs/installation.md).

## Neu in 1.0.21

Kleine Discord-Kamera-/Bildschirmframes können jetzt auch mit einem einzelnen RTP-Paket über ihren 90-kHz-Zeitverlauf erkannt werden. Paketverluste, große Keyframes und vertauschte Pakete setzen eine bestätigte Videozuordnung nicht mehr ständig zurück. Sprache bleibt getrennt. [Änderungen](docs/release-notes-1.0.21.md), [Erkennung und Grenzen](docs/video-detection.md).

## Neu in 1.0.20

Die Videoerkennung umfasst bekannte Lieferhosts von YouTube, Netflix, Twitch Live, Disney+, Prime Video, Vimeo und Apple TV sowie beobachtbare Kamera-/Bildschirmübertragungen nativer Discord-/Teams-Clients. Kleine bestätigte Konferenz-Videopakete werden von Sprache getrennt; die Videozuordnung aktiver Streamingverbindungen bleibt auch nach 30 Minuten erhalten. Vertauschte TLS-Handshake-Segmente werden begrenzt zusammengesetzt. [Änderungen](docs/release-notes-1.0.20.md), [Abdeckung und Grenzen](docs/video-detection.md).

Die automatische lokale [ELFIX-Medienmeldung](docs/elfix-integration.md) aus 1.0.19 bleibt erhalten. Spiele und Sprache behalten Vorrang; die allgemeine Priorisierung bleibt auch ohne laufendes Spiel aktiv. DYNEX begrenzt weiterhin keine Bandbreite. Eine vollständige Erkennung beliebig verschlüsselter Anbieterpfade wird nicht behauptet.

## Reine Priorisierung

- Erkannte ausgehende interaktive Spielpakete erhalten **DSCP CS4 (32)**, geeignete Sprachpakete **EF (46)** und nachgewiesene Videoverbindungen **AF41 (34)**. Nur IP-Prioritätsfelder und die erforderliche IPv4-Headerprüfsumme ändern sich. Transportheader, Paketinhalt und ECN bleiben erhalten.
- **DYNEX begrenzt keine Downloads oder Uploads.** Es gibt keine gemeinsamen Budgets, adaptiven Kürzungen, manuellen Programm- oder Gerätelimits, Zeitpläne für Limits oder Downloadpausen.
- Der Helfer leitet alle Pakete unmittelbar weiter. Alte Limitparameter können keine Warteschlangen oder Drosselung aktivieren.
- Der Router muss DSCP auswerten, damit Markierungen dort Vorrang bewirken. DYNEX richtet den Router nicht ein und garantiert keinen bestimmten Ping. SQM/CAKE oder andere Bandbreitenregelung gehört auf den Router.
- Priorisierung benötigt keine Geschwindigkeitsmessung. Ein Speedtest wird ausschließlich manuell als Diagnose gestartet.

## Start und bestehende Installationen

Installiere DYNEX auf jedem Windows-PC, auf dem du lokale Spielpakete markieren möchtest. Im privaten LAN werden andere DYNEX-Geräte automatisch erkannt. Bestehende private Gruppen bleiben erhalten. Der signierte Windows Packet Filter und die gezielte private LAN-Firewallfreigabe bleiben für Paketverarbeitung und Geräteverbindung erforderlich. Die Einrichtung kann eine Windows-Bestätigung verlangen; die normale Oberfläche läuft ohne Erhöhung.

Bestehende Einstellungen, importierte Sicherungen und gespeicherte Netzwerkprofile werden auf den Betrieb ohne Limits migriert. Geräte, Aliasnamen, Kopplungen, Spiele und Messhistorien bleiben erhalten. Ältere DYNEX-Versionen auf anderen Geräten können ihre eigenen Limits weiterhin anwenden; aktualisiere alle beteiligten Geräte.

Ein geeignetes Spiel muss tatsächlichen Netzwerkverkehr haben. Ohne passenden Spielverkehr bleibt die Spielbeobachtung passiv; die allgemeine Priorisierung für Sprache und erkannte Videos bleibt bei eingeschaltetem Priorisierungsschalter aktiv. Abschalten, Diagnose-Speedtest und Beenden geben die Capture-Sitzung frei. Erkannte laufende Spiele werden automatisch auch im Hintergrund beobachtet; der Fokus bestimmt nur die angezeigte Spielauswahl. Unbekannte oder mehrdeutige Prozesszuordnung führt nicht zu einer Markierung. [Spielerkennung](docs/game-detection.md), [Priorisierung](docs/qos.md).

## Android

Die Android-App bleibt eine energiesparende WLAN-Begleit-App mit Geräteverbindung. Sie erkennt keine Handyspiele und behauptet keine eigene Spielpaketpriorisierung. Es werden weder Handy-Downloads begrenzt noch neue VPN-Tunnel für Limits gestartet.

Ein bereits in AdGuard eingetragener DYNEX-Proxy bleibt als **unbegrenzte Durchleitung** verfügbar, damit ein Update die Internetverbindung nicht unterbricht. Neue Installationen benötigen keinen AdGuard-Assistenten. Ohne Messpfad bleibt der Handyverbrauch ausdrücklich unbekannt. [Android-Verhalten](docs/android.md).

## Ansichten und Daten

Die Übersicht zeigt bestätigte lokale Priorisierung, Spiel-/Sprach-/Videopaketzähler, aktuelle Datenraten und Router-/Internetping. Geräteansicht, Aliasverwaltung, Analyse und Verbrauchsverlauf bleiben erhalten. Netzwerkprofile werden im Hintergrund weiter verwendet. Unbekannte Messwerte erscheinen als **—**, gemessener Leerlauf als **0,0 Mbps**. Netzwerkprofile speichern Messwerte und Verbindungseinstellungen; alte Limitfelder sind wirkungslos. [Desktop-Funktionen](docs/desktop-features.md).

Optionale Hintergrundupdates, Einstellungen sichern/wiederherstellen und Windows-Autostart bleiben verfügbar. Der Updater lädt sein eigenes Installationspaket weiterhin schonend herunter; daraus entsteht kein Limit für andere Programme. [Updates](docs/updates.md).

## Entwicklung und Prüfung

Die Projekte liegen unter `src/`, die isolierten ausführbaren Tests unter `tests/`. Die Windows-Pakettests prüfen Markierungen für IPv4/IPv6, Nutzdatenintegrität, unmittelbare Weiterleitung trotz alter Limits, Prozesszuordnung, ACKs, Watchdog und Freigabe. Runtime-Prüfungen kontrollieren Migration und den Betrieb ohne automatische Speedtests. Ein Test mit simuliertem Paketgerät ersetzt keinen Test mit echtem Filtertreiber, Router und zwei physischen PCs.

[Architektur](docs/architecture.md) · [Paketverarbeitung](docs/shaping.md) · [Installation](docs/installation.md) · [Anti-Cheat-Grenzen](docs/anticheat.md)
