# DYNEX
### Dynamic Network Experience

DYNEX ist eine native Windows-11-App für gemeinsames Gaming- und Downloadmanagement im privaten LAN. Die WPF-Oberfläche verwendet Schwarz, Dunkelrot `#C91F32` und Hellrot `#FF4655`.

**Releaseziel 0.4.1.** Das Setup enthält DYNEX samt erforderlichem Helper, aber keinen Netzwerkfilter-Installer, Kernel-Treiber, WinDivert oder native Filter-DLL. Der Filter wird nur nach ausdrücklicher Einrichtung in DYNEX geladen: Die App lädt das offizielle Windows Packet Filter MSI, prüft den festgelegten SHA-256 und Windows-Authenticode-Vertrauen und startet die interaktive Windows-Einrichtung mit UAC. Das kann die Netzwerkverbindung kurz unterbrechen; ein Neustart wird nicht erzwungen.

## Schnellstart

1. Installiere DYNEX auf den PCs im selben privaten LAN und starte die App. AutoLAN ist standardmäßig aktiv; neue DYNEX-Identitäten werden beim ersten Kontakt im privaten LAN vertraut. Ein Gruppencode ist für den normalen Start nicht nötig.
2. DYNEX ermittelt die Downloadgeschwindigkeit beim ersten Start automatisch per Speedtest und speichert den Wert. Nach erfolgreicher Messung wird er auch bei Neustarts weiterverwendet; über **Erneut messen** lässt er sich aktualisieren. Das Hintergrundbudget beträgt standardmäßig 65 %; pro erkanntem Gaming-PC wird automatisch `min(10 Mbit/s, 5 % der Kapazität)` reserviert.
3. Richte den Netzwerkfilter einmal über die **Einrichten**-Aktion in DYNEX ein und bestätige die offizielle Windows-Installer-UAC-Abfrage. Danach aktiviert **Spiele & Sprachchat priorisieren und große Downloads begrenzen** den Netzwerkschutz.

Die optionale private Gruppe bleibt für bewusst gemeinsame Kopplung verfügbar. Details und Rückweg stehen in [Installation](docs/installation.md).

Der Download-Speedtest verwendet den [offiziellen Cloudflare-Messendpunkt](https://github.com/cloudflare/speedtest) und überträgt höchstens 256 MiB in maximal 10 Sekunden Messzeit. DYNEX pausiert dafür seine lokale Downloadbegrenzung und setzt sie anschließend mit dem gespeicherten Ergebnis fort. Für ein brauchbares Ergebnis sollten andere Downloads pausieren. Gemessen wird die auf diesem PC erreichbare Geschwindigkeit; WLAN, VPN und andere Geräte können sie beeinflussen. Bei einem Fehler bleibt der bisherige Wert erhalten; ohne erfolgreiche Messung ist er als vorläufig gekennzeichnet. Es gibt keine Wiederholungsschleife: ein weiterer automatischer Versuch erfolgt erst beim nächsten App-Start, solange noch kein Ergebnis gespeichert wurde. Bestehende Einstellungen ohne Messergebnis werden ebenfalls einmal gemessen.

## Verhalten und Grenzen

Sobald ein PC spielt, teilen sich die verbundenen DYNEX-PCs **ein gemeinsames Downloadbudget**. Sie tauschen ihre Kapazität und Reserven verschlüsselt aus und verwenden den kleinsten daraus errechneten Topf. Unterschiedliche Speedtest-Ergebnisse erzeugen dadurch nach dem Statusaustausch kein mehrfach vergebenes Budget. Jeder PC bekommt einen gleichen Anteil; ein manuelles Limit oder die adaptive Pingreserve kann seinen Anteil weiter senken. Auch der Gaming-PC bekommt einen Downloadanteil für seine eigenen Hintergrunddownloads. Ungenutzte Anteile bleiben frei; sie werden nicht zusätzlich an andere PCs vergeben.

Beispiel: Bei 80 Mbit/s, 65 % Hintergrundanteil und 4 Mbit/s Spielreserve stehen insgesamt 48 Mbit/s für große Downloads bereit. Bei drei PCs sind das höchstens 16 Mbit/s je PC. Zwei Downloader können gleichzeitig laden; Spiel- und Discordpakete umgehen auf ihrem PC die Downloadwarteschlange. **Verbundene PCs** zeigt den gemeinsamen Topf, das lokale aktive Limit und auf wie vielen PCs die Begrenzung bestätigt ist. Alle PCs benötigen DYNEX 0.3.1 oder neuer und eine eingeschaltete Downloadbegrenzung. Die Übertragung dauert ungefähr zwei Sekunden; getrennte, nicht gegenseitig erreichbare Gruppen können keine gemeinsame Obergrenze gewährleisten.

Unter **Verbundene PCs** zeigt DYNEX den aktuellen Download und Upload jedes verbundenen DYNEX-PCs in Mbit/s (Mbps), getrennt vom bestätigten Downloadlimit. Die Messung und Übertragung erfolgen ungefähr alle zwei Sekunden. DYNEX muss auf jedem beteiligten PC laufen; AutoLAN und die private Gruppe unterstützen die Anzeige. Beide Richtungen beziehen sich auf den am jeweiligen PC gemessenen Netzwerkadapter und enthalten auch Heimnetzverkehr, nicht nur Internetdownloads. Fehlende Messwerte erscheinen als **—**, ein gemessener Stillstand als **0,0 Mbps**. Ältere DYNEX-Versionen können nur den Download melden; für den Upload müssen sie aktualisiert werden. PCs ohne DYNEX werden nicht erfasst.

DYNEX erkennt bekannte Spielprozesse, benutzerdefinierte Namen, positiv klassifizierte Steam-Spiele und Demos sowie Spiele mit passenden lokalen Epic- und GOG-Starteinträgen. Wallpaper Engine, Launcher und bekannte Hilfsprogramme bleiben ausgeschlossen. Das ist eine Prozesserkennung: Der Prozess kann im Menü sein. DYNEX bestätigt weder Multiplayer noch Match oder aktive Runde. Eine lückenlose Erkennung aller existierenden Spiele ist nicht möglich; unbekannte Titel können unter **Erweitert** ergänzt werden. Details stehen unter [Spielerkennung](docs/game-detection.md).

Der eingeschaltete Netzwerkschutz priorisiert erkannte Spiel- und Sprachpakete lokal und markiert geeignete ausgehende Pakete für QoS. Das läuft auch ohne aktives Spiel oder Downloadlimit. Nicht nur bekannte Sprach-Apps, sondern auch konservativ erkannte RTP-Audiofolgen werden berücksichtigt; unbekannte verschlüsselte WebRTC-Flows lassen sich nicht universell zuordnen. Router müssen die Markierungen unterstützen, damit daraus dort ein Vorrang entsteht. [QoS-Verhalten und Grenzen](docs/qos.md) beschreibt Klassen, Erkennung und Abschaltung.

Die eckige Oberfläche passt ihre Anordnung und Lesbarkeit an die Fensterbreite an. Im Ping-Verlauf zeigt die Mausposition den gespeicherten Zeitpunkt sowie Router- und Internetping; fehlende Antworten bleiben ausdrücklich als fehlend erkennbar.

Große Downloads werden erst nach mindestens 250 ms und 256 KiB berücksichtigt. Erkanntes Gaming auf demselben PC bleibt automatisch ungedrosselt. Spiele, Sprach-/Videokonferenzprogramme (Discord, Teams, Zoom, TeamSpeak, Mumble), reine TCP-ACKs, DNS, LAN-Verkehr sowie unbekannte oder nicht mehr zuverlässig zuordenbare Flows bleiben ausgenommen.

Der NDISRD-Netzwerkfilter ist ein systemweiter NDIS-Filter. Andere Programme können denselben Adapter beanspruchen; bei erkannter Koexistenz-/Öffnungsstörung bricht DYNEX ab, statt den Adapter zu übernehmen. Installation, native Downloadbegrenzung, Prozessausnahme und Wiederfreigabe wurden auf einem PC praktisch geprüft. Steam-Download, laufender Sprachchat, Anti-Cheat und drei physische PCs im gemeinsamen Match sind noch nicht gemeinsam geprüft. DYNEX verspricht keine bestimmte Pingzahl oder garantierte WAN-Priorisierung; Routerwarteschlangen und Uploads werden nicht gesteuert.

Die MSI-Datei wird nicht mit DYNEX weitergegeben. Der Hersteller nennt private/akademische Nutzung und Evaluation als kostenlos; kommerzielle Nutzung oder Weitergabe benötigt eine gesonderte Lizenzprüfung. DYNEX entfernt den Filter bei seiner Deinstallation nicht, weil andere Programme davon abhängen können.

Weitere Details: [Shaping](docs/shaping.md), [QoS](docs/qos.md), [Architektur](docs/architecture.md), [Backend](docs/ndis-backend.md) (falls im Quellpaket vorhanden) und [ausgeführte Tests](docs/TESTING.md).
