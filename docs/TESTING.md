# DYNEX 0.4.1 · Prüfstatus

## Ping-Lücken unter Windows (0.4.1)

Die Ping-Messung nutzte bisher 900 ms. Windows lieferte dabei auf diesem Rechner teilweise `TimedOut` schon nach 15–109 ms, auch bei abgeschaltetem DYNEX-Capture. Die Windows-Implementierung von [OpenJDK 25](https://github.com/openjdk/jdk/blob/jdk-25-ga/src/java.base/windows/native/libnet/Inet4AddressImpl.c#L318-L343) dokumentiert inkonsistentes Verhalten von `IcmpSendEcho` und seinen Ableitungen unter 1000 ms. DYNEX verwendet deshalb jetzt 1000 ms; Messfehler werden weiterhin als Lücken gezählt, ohne Wiederholung oder Ersatz durch ältere Werte.

Der gleichzeitige native Vergleich vom 14.10.2026 unter `artifacts/qa-ping-timeout-20261014-142110` umfasste pro Timeout und Ziel je 50 Messungen bei ausgeschaltetem und eingeschaltetem Filter. Bei 900 ms kamen 97 von 100 Internet-Antworten an; die drei Fehlermeldungen erschienen bereits nach 14,8–16,6 ms. Bei 1000 ms kamen 100 von 100 Internet- und 100 von 100 Router-Antworten an. Die erfolgreichen Internet-Antworten lagen im Mittel bei etwa 19 ms; die längere Timeoutgrenze verlängert erfolgreiche Messungen nicht.

Zusätzlich bleibt bei laufenden Limitänderungen und beim Wechsel zwischen Limit und reiner Priorisierung jetzt dieselbe native Capture-Sitzung erhalten. Dadurch wird die Paketwarteschlange nicht mehr bei jeder adaptiven Änderung verworfen. 153 Shaper-Prüfungen bestehen, einschließlich wiederholter Limitwechsel über echtes Helper-IPC mit Testgeräten, Kontrollpaket-Weitergabe und bestätigter Freigabe. Native Filtertabelle, Adaptereinstellungen und Treiber wurden für diese Korrektur nicht verändert.

Die anschließende native Prüfung des korrigierten Helpers steht unter `artifacts/qa-native-0.4.1`: In den drei aktiven Phasen (zweimal reine Priorisierung, einmal wechselnde Limits) kamen alle 90 Router- und alle 90 Internet-Antworten an. Die drei Phasen ohne Capture ergaben jeweils 89 von 90 Antworten; eine Router- und Internetprobe scheiterten gleichzeitig nach etwa 950 ms. Solche tatsächlichen Messfehlschläge bleiben sichtbar. Der Downloadtest bestand: 39,42 Mbit/s Basisrate, 6,72 Mbit/s bei 8-Mbit/s-Limit, 58,69 Mbit/s für den geschützten Prozess und 47,16 Mbit/s nach Freigabe. Das sind einzelne Testläufe ohne Ping- oder Durchsatzgarantie.

Der Release-Build und der Smoke-Test der gepackten 0.4.1-App unter `artifacts/qa-packaged-0.4.1` bestanden, einschließlich Ping-Hover, Fenstergrößen, Einstellungen und verschlüsselten Peer-Nachrichten. Die nativen Testskripte haben DYNEX nach der Prüfung wieder gestartet; die Produktionskonfiguration blieb erhalten.

0.4.1 wurde anschließend installiert: Setup-Exitcode 0, Hashvergleich von App/Helper erfolgreich, Benutzereinstellungen bytegleich. Die installierte Live-Diagnose um 14:30 Uhr erkannte Marvel Rivals, bestätigte Priorisierung und 50,85 Mbit/s Downloadbudget und zeigte bei vier frischen Verlaufspunkten 2 ms Router-/20 ms Internetping ohne fehlende Antworten. Ergebnisdateien: `artifacts/qa-installed-0.4.1`. DYNEX blieb geöffnet. Das kurze Startfenster ist zusätzlich zu den längeren Vergleichsmessungen dokumentiert und ersetzt keine Langzeitmessung.

## QoS, Spielerkennung und Oberfläche (0.4.0)

Am 14.10.2026 wurden die geänderten Paketpfade mit **145 Shaper-, 34 Bulk- und 58 Traffic-Prüfungen** getestet. Abgedeckt sind bidirektionales NDIS-Routing, IPv4-Prüfsumme, IPv6-DS-Feld, ECN, VLAN, PID-Identität, RTP-Positiv-/Negativfälle, begrenzte Sprachpriorität, QoS ohne Downloadlimit, Zähler nach erfolgreichem Send sowie bestätigter Stop und Fehlerfreigabe im echten Helper-IPC und Desktop-Controller. Die automatischen Pakettests verwenden Testgeräte; die separate native Prüfung ist unten beschrieben.

**143 Spielerkennungsprüfungen** bestanden, einschließlich Steam-Demos, Punktnamen, Epic/GOG-Manifeste, Toolausschlüsse und ungültige/veraltete Installpfade. Die rein lesende lokale Prüfung fand 150 positive Steam-Installationen einschließlich Demos. Epic/GOG sind anhand synthetischer Fixtures geprüft; auf diesem PC waren keine gültig vorhandenen Epic-/GOG-Installationen verfügbar.

Die echten Filtertests stehen in `artifacts/qa-native-0.4.0/{qos,live,watchdog}`. QoS-only ohne Downloadcap war aktiv; ein 4-MB-Download erreichte 23,93 Mbit/s insgesamt, ein synthetischer 1-MB-Upload wurde mit HTTP 200 bestätigt. Während des geschützten Prozessprobes stieg der Spielzähler um 438, während 80 synthetischer RTP-Audiopakete an den lokalen Gateway-Testport der Sprachzähler um 180. Diese Zähler sind sitzungsweit und können Verkehr anderer laufender Apps enthalten; sie sind kein isolierter Nachweis der Anzahl exakt dieser Testpakete und kein Nachweis der Markierung auf einem entfernten Router.

Mit gleichzeitigen Hintergrundverbindungen erreichte der Testdownload unter einem gemeinsamen lokalen 8-Mbit/s-Limit 1,57 Mbit/s insgesamt beziehungsweise 1,27 Mbit/s im zweiten Abschnitt. Der geschützte Prozess erreichte 54,22 Mbit/s, nach Freigabe waren es 52,80 Mbit/s. Das Limit gilt gemeinsam für die erkannten großen Downloads, nicht separat für jeden Testprozess. QoS-Stop und Watchdog-Freigabe wurden bestätigt; nach dem Watchdog funktionierte ein weiterer Download mit 48,07 Mbit/s. Die Produktionskonfiguration blieb bytegleich und die installierte Anwendung wurde wieder gestartet. Einmalige Durchsatzwerte sind keine Leistungsgarantie.

29 Runtime-Prüfungen bestanden nach der Integration: persistierte Kapazität, automatischer Erst-Speedtest, Parallelaufrufe, Einstellungen, Abbruch und Lebenszyklus. Der finale UI-Smoke unter `artifacts/qa-ui-0.4.0` bestand bei 900/1000/1240/1440/1920/2560 Pixeln und maximiert innerhalb der Windows-Arbeitsfläche. Er prüft Hoverwerte, Zeitstempel, Cursor, fehlend versus 0 ms, Randzuordnung, Größenwechsel, verschlüsselten Peer-Transport, UI-Zellen, Einstellungen, DPAPI und sauberes Beenden. Die echten WPF-Bilder einschließlich kleiner und großer Gerätetabellen wurden visuell geprüft. Eine zweite physische Anzeige mit anderer DPI-Skalierung wurde nicht getestet.

Die folgenden Abschnitte dokumentieren frühere, getrennt ausgeführte Prüfungen.

### Paket und installierte Version

Der 0.4.0-Installer wurde am 14.10.2026 erfolgreich erstellt und installiert. Der Smoke-Test der gepackten Anwendung bestand unter `artifacts/qa-packaged-0.4.0`; anschließend wurde ausschließlich der Aufnahmezeitpunkt der Live-Diagnose um einen abgeschlossenen WPF-Layoutdurchlauf ergänzt und das Paket neu gebaut. Setup-Exitcode 0, übereinstimmende Hashes von installierter Anwendung/Helper und veröffentlichten Dateien sowie bytegleich erhaltene Benutzereinstellungen sind in `artifacts/qa-installed-0.4.0/installation.txt` dokumentiert.

Die abschließende Live-Diagnose vom 14.10.2026, 13:55 Uhr, lieferte drei frische Runtime-Snapshots und bestätigte aktive Priorisierung. Ohne erkanntes Spiel blieb das automatische Downloadlimit erwartungsgemäß aus. Das frisch gerenderte `dashboard-live.png` zeigt Verlaufslinien und Gerätezeile vollständig; `result.txt` und `live-diagnostics.json` enthalten den zugehörigen Status. Die installierte Anwendung blieb geöffnet. Die Priorisierung lässt sich über die entsprechende Einstellung deaktivieren; das Beenden gibt den Filter mit bestätigter Freigabe frei.

Seit 0.3.0 wird ein erforderlicher Shaper-Helper mit direkter `DeviceIoControl`-Anbindung an NDISRD verpackt. Das NDISRD-MSI ist kein Setupbestandteil; eine ausdrückliche Aktion in DYNEX lädt, hasht und authenticodeprüft es und startet anschließend die interaktive Windows-Einrichtung.

## Gemeinsames Budget und Paketvorrang (0.3.1)

131 Core- und 59 Adaptive-Prüfungen bestanden. Drei unabhängige Instanzen tauschen über echte verschlüsselte UDP-Loopback-Verbindungen unterschiedliche Kapazitäten und Reserven aus (200/160/240 Mbit/s). Sie berechnen alle einen gemeinsamen Pool von 136 Mbit/s und je 45,33 Mbit/s. Die über UDP zurückgemeldeten simulierten Limitbestätigungen ergeben zusammen 135,99 Mbit/s. Gamerwechsel, zwei Gamer und Ablauf des langsamsten PCs sind abgedeckt; ebenso fehlende/ungültige/alte Vorschläge und die 2048-Byte-Paketgrenze. Das sind drei Instanzen auf einem Rechner, keine drei physischen LAN-PCs und keine native Begrenzung dieser simulierten Anteile.

32 Bulk-Prüfungen bestanden. Der neue Integrationstest führt zwei unterschiedliche als groß erkannte Downloadprozesse durch den echten Klassifizierer und eine volle `PacketPump`-Downloadwarteschlange. Alle 128 synthetischen UDP-Spiel-/Discordpakete über IPv4 und IPv6 werden sofort am Downloadpuffer vorbeigeleitet. Es wird dabei kein Kernel-Filter geöffnet. Der Test belegt die lokale Warteschlangentrennung, keine Priorität im Router und keinen festen Spielping.

69 Shaper-Prüfungen bestanden. Neu abgesichert sind Reset-/Flushfehler, bestätigter Stop, ausbleibende/alte Bestätigungen, geschlossene Pipes, Startabbruch und EOF. Die echte Helper-Kommandoschleife und der Desktop-Controller werden mit Testgeräten und Pipes ausgeführt. Fehler bei der Freigabe bleiben sichtbar und blockieren unbestätigtes Wiederaktivieren; ein Cleanupfehler im finalen Pfad liefert Exitcode 1. Der native Durchsatznachweis der vorherigen Version ist unten getrennt dokumentiert.

## Status dieser Dokumentation

Am 14.10.2026 bestanden: Desktop-/Helper-Release-Build (keine Compilerwarnungen oder -fehler) und 370 automatisierte Prüfungen: 82 Core-, 59 Adaptive-, 58 Traffic-Ownership-, 52 Spielerkennungs-, 43 Shaper-, 28 Bulk-Policy-, 19 Speedtest- und 29 Runtime-Prüfungen. Die Shaper-Suite verwendet synthetische Pakete und einen echten lokalen TCP-Empfänger: Pacing, voller Downloadpuffer bei sofortigem Sprachchat-Durchlass, Abschaltung und NDIS-Strukturlayouts wurden geprüft. Zusätzlich wurde auf dem realen Benutzer-PC (Ethernet 3) der signierte NDISRD 3.6.2.1 installiert und der erhöhte Helper per UAC gestartet. Beobachtete kumulative Ethernet-Zähler: 14,9 MB empfangen, 7,45 MB gesendet, 6,08 MB umgangen. Bei 43,67 Mbit/s Basisrate ergab ein 8-Mbit/s-Limit 6,74 Mbit/s insgesamt bzw. 5,08 Mbit/s im stationären Abschnitt; ein geschützter Prozess erreichte 50,90 Mbit/s, nach Deaktivierung 59,02 Mbit/s. Das bestätigt einen nativen Testlauf, aber keine allgemeine Leistungsgarantie.

Die gepackte Desktop-App bestand den isolierten Smoke-Test: echte Messwerte, WPF-Rendering bei 1440/1000 Pixeln, Einstellungsvalidierung, DPAPI-/Gruppenwechsel, Diagnoseexport und sauberes Beenden. Die gerenderten Ansichten wurden visuell geprüft. Die Anwendungssignaturprüfung akzeptiert das gepinnte offizielle MSI und lehnt eine unsignierte Textdatei ab. Hash und MSI-/Treiberkatalogsignaturen wurden separat geprüft. Gegenseitige Pipe-PID-Prüfung, gehaltenes MSI-Dateihandle und Statusfreigabe bei veralteter Prozesszuordnung wurden im Code nachgeprüft.

Ein separater Watchdog-/IPC-Ausfalltest ließ den Helper nach 7,18 Sekunden ohne Herzschlag mit Exitcode 0 sauber beenden; anschließend wurde ein echter 4-MB-Download mit 53,48 Mbit/s gemessen. Das war kein harter Prozesskilltest.

## Einmalige Geschwindigkeitsermittlung (14.10.2026)

Die neuen Console-Suites `tests/Dynex.SpeedTest.Tests` (19 bestandene Checks) und `tests/Dynex.Runtime.Tests` (29 bestandene Checks) prüfen den Streaming-Download sowie automatische Erstermittlung, gespeicherte Ergebnisse, Altprofile, Fehler und Abbruch, parallele Messaufrufe und während der Messung wartende Einstellungsänderungen. Die HTTP-Antworten und Messwerte dieser Tests sind simuliert; Runtime-Profile und Netzwerkprobes sind isoliert beziehungsweise auf Loopback begrenzt. Ausführen mit `dotnet run --project tests/Dynex.SpeedTest.Tests -c Release` und `dotnet run --project tests/Dynex.Runtime.Tests -c Release`.

Zusätzlich bestanden: Desktop-Release-Build, die 59 Adaptive-Checks und der isolierte Desktop-Smoke-Test inklusive WPF-Rendering bei 1440/1000 Pixeln, Speicherprüfung und sauberem Beenden. Die gerenderte Einstellungsansicht wurde visuell geprüft. Ein echter 1-KiB-Request an den Cloudflare-Endpunkt bestätigte HTTP 200, unkomprimierte Binärdaten und die angeforderte Länge. Beim anschließenden echten Start von 0.3.0 wurden 83,05 Mbit/s ermittelt und gespeichert; Marvel Rivals wurde erkannt und ein aktives Downloadbudget von 49,83 Mbit/s bestätigt (`artifacts/qa-installed-0.3.0`). Die erneute Messung mit bereits aktivem NDISRD-Filter ist noch nicht praktisch geprüft.

## Up-/Download je verbundenem PC (14.10.2026)

- Core-Suite: 98 Checks bestanden. Uploadwerte durchlaufen beide verschlüsselten Peerprotokolle; abgedeckt sind fehlende Felder älterer Peers, echte Nullwerte, ungültige Werte, Aktualisierung und das Verwerfen alter Durchsatzmessungen trotz weiterlaufendem Heartbeat.
- Adaptive-Suite: alle 59 Prüfungen der Bandbreitenverteilung und Gaming-Rückmeldungen bestanden.
- Desktop-Release-Build: ohne Warnungen oder Fehler. Der isolierte `--smoke-test` prüft zusätzlich den Weg von einem verschlüsselten Loopback-Peer über den Runtime-Snapshot bis in die WPF-Zellen und den Diagnoseexport, einschließlich 0/fehlender Werte und Geräteablauf nach Verbindungsende. Die Tabellen werden bei 1440 und 900 Pixel Fensterbreite geprüft. Die `peer-traffic-fixture-*.png`-Ansichten verwenden ausdrücklich beschriftete synthetische Messwerte.
- Keine physische Zwei-PC-Abnahme und keine neue Filter-/Treiberprüfung für diese Anzeige.

## Auswahl laufender Spielprozesse (14.10.2026)

Die GameDetection-Suite besteht mit 152 Checks, davon neun für die Prozessauswahl: Filterung und Deduplizierung, bestehende `.exe`-Einträge, Erhaltung manueller Eingaben, Namens-/Längengrenzen und die tatsächliche Erkennung eines hinzugefügten Namens. Der Desktop-Release-Build ist ohne Warnungen oder Fehler erfolgreich.

Der isolierte Desktop-Smoke-Test öffnet das Dropdown mit der echten laufenden Prozessliste und prüft Aktualisierung, Hinzufügen erst nach Auswahl, Duplikatschutz sowie das Speichern im Testprofil. Dropdown und Oberfläche wurden bei 900 und 1440 Pixeln gerendert und visuell kontrolliert. Ergebnisse: `artifacts/qa-process-picker-final/process-picker-result.txt` und `process-picker*.png`. Für diese Prüfung wurde kein Spiel gestartet und das normale Benutzerprofil nicht geändert. Testprofile verwenden eigene lokale UDP-Ports, damit parallele Smoke-Tests nicht denselben Empfangsport belegen. Nach dieser Trennung bestanden der vollständige Smoke-Test und alle 29 Runtime-Checks.

## Noch notwendige Prüfungen

- Noch nicht ausgeführt: Steam-Downloadvergleich, Discord-/Sprachgesprächvergleich, VPN-/Filterkoexistenz, Anti-Cheat-Prüfung und Zwei-PC-Abnahme. Der native Durchsatztest ist ein einzelner Testlauf und ersetzt diese Szenarien nicht.

Es gibt keine Zusage für eine feste Pingzahl, tatsächliche WAN-Priorisierung oder Anti-Cheat-Kompatibilität. Siehe [Shaping und Grenzen](shaping.md) und [Installation](installation.md).

## Installierte Live-Diagnose

`DYNEX.exe --verify-live <absoluter-Ausgabeordner>` startet das normale Benutzerprofil und lässt die Anwendung geöffnet. Nach mindestens drei neu eingetroffenen Runtime-Snapshots werden `live-diagnostics.json`, `dashboard-live.png` und `result.txt` im Ausgabeordner abgelegt; insgesamt wartet der Diagnosepfad höchstens 45 Sekunden. Der Screenshot verwendet die aktuelle Fenstergröße; der Diagnosemodus ändert keine Fensterabmessungen. Bei einem Diagnosefehler wird zusätzlich `error.txt` geschrieben. Der Modus führt keine Smoke-Tests oder zusätzlichen Speedtests aus und speichert keine Diagnoseeinstellungen. Beim erstmaligen normalen Profilstart läuft jedoch die unveränderte automatische Kapazitätsmessung; diese kann wie im normalen Start Einstellungen speichern.
