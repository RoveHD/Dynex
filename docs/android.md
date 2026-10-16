# DYNEX für Android

DYNEX Android verbindet sich im WLAN mit DYNEX-PCs. Die App erkennt selbst keine Spiele. Sie liest den authentifizierten Gaming-Status und berechnet aus dem vom PC gemessenen, freigegebenen Leitungsbudget ihren Downloadanteil. Ein optionales Handy-Limit ist nur eine zusätzliche Obergrenze. Ohne gemessenen Budgetvorschlag oder aktives PC-Gaming wird kein Limit erfunden. Das Limit gilt nur für Downloads; Mobilfunk bleibt unbegrenzt.

## Voraussetzungen und Build

Der lokale Build wurde mit .NET SDK 9.0.315 und Android workload 35.0.78 sowie den Repository-Tools unter `.tools` eingerichtet: Android API 35/build-tools 35.0.0 und Microsoft JDK 17.0.14. SDK und Workload müssen zuvor installiert sein; die Skripte installieren sie nicht und ändern weder `PATH` noch globale SDK-Einstellungen. `prepare-android.ps1` stellt das offizielle hev-socks5-tunnel 2.19.0 AAR bereit, prüft den fest eingetragenen SHA-256 und extrahiert nur die beiden benötigten ABI-Bibliotheken.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/prepare-android.ps1
powershell -ExecutionPolicy Bypass -File scripts/build-android.ps1 -Configuration Debug
```

Für einen Release-Konfigurationsbuild kann `-Configuration Release` verwendet werden. Ohne expliziten Keystore erzeugt das Skript ein `*-TESTSIGNATURE.apk` in `artifacts/android/`. Für eine dauerhaft signierte Release-APK werden zusätzlich `-SigningKeyStore`, `-SigningKeyAlias`, `-SigningStorePasswordReference` und `-SigningKeyPasswordReference` gemeinsam angegeben. Passwortreferenzen akzeptieren ausschließlich `env:NAME` oder `file:PFAD`; Geheimnisse gehören nicht in die Kommandozeile oder ins Repository. Der Keystore muss für spätere Updates erhalten bleiben. Die signierte Ausgabe erhält eine SHA-256-Datei; der Build veröffentlicht sie nicht automatisch.

Bei Passwortdateien für Store und Key zwei getrennte Dateien verwenden, auch wenn beide dasselbe Passwort enthalten: `apksigner` liest eine gemeinsam referenzierte Datei sequenziell. Alternativ dieselbe `env:NAME`-Referenz verwenden. Das Skript baut seriell mit `-m:1` und übergibt SDK/JDK nur für diesen Build. Auch Debug-APKs enthalten die Assemblies vollständig und verwenden die normale Android-Laufzeit ohne Soft-Debugger. Mit dem Soft-Debugger der hier verwendeten Toolchain blockierten schon unabhängige Thread-/Timer-Prüfungen ohne Produktlogik.

## AdGuard-Einrichtungsassistent

Beim ersten Öffnen mit installiertem AdGuard startet der Assistent. Ohne AdGuard erscheint er nicht; die zugehörige Einrichtung ist deaktiviert. Wird AdGuard später installiert, bietet DYNEX die Einrichtung beim nächsten Öffnen an. Bei installiertem AdGuard bleibt der Assistent unter **Einstellungen → AdGuard einrichten** erreichbar. Er benötigt AdGuard für Android ab Version 4 und führt durch drei Schritte:

1. AdGuard öffnen und unter **App-Verwaltung → DYNEX → Datenverkehr durch AdGuard leiten** DYNEX selbst ausnehmen, damit keine Proxy-Schleife entsteht. Anschließend unter **Einstellungen → Allgemein → Erweiterte Einstellungen → Automatisierung** die Automatisierung freigeben.
2. Das dort angezeigte Automatisierungs-Passwort einmal lokal in DYNEX eingeben. DYNEX speichert es mit einem eigenen, nicht exportierbaren Android-Keystore-Schlüssel verschlüsselt außerhalb von Android-Backups. DYNEX startet zuerst seinen lokalen Listener und fordert dann über AdGuards dokumentierte Schnittstelle die Einrichtung und Aktivierung des SOCKS5-Proxys an. Ein bisher ausgewählter Proxy wird dabei ersetzt; Werbe- und DNS-Filter werden nicht geändert.
3. Eine Browserseite oder App laden. Der Assistent bestätigt Weiterleitung erst nach tatsächlich über den Proxy übertragenen Downloadbytes. Eine versendete Anfrage, ein offener Listener oder eine bloße Verbindung reichen nicht als Bestätigung.

Der lokale Proxy lauscht ausschließlich auf `127.0.0.1:37831`, mit dem Namen `dynex` und einem zufällig erzeugten eigenen Proxy-Passwort. Diese Werte trägt DYNEX ein; sie müssen nicht abgetippt werden. AdGuard behält seine lokale VPN-Verbindung; DYNEX braucht dafür keine eigene Android-VPN-Freigabe. Die Ausnahme für DYNEX selbst ist nötig: Im Test mit AdGuard 4.14.2 funktionierte die Weiterleitung erst danach. AdGuards öffentliche Automatisierung bietet dafür keine dokumentierte Aktion; der Assistent erklärt diesen einmaligen Schritt. Nur tatsächlich durch AdGuard und dessen Proxy geleitete Apps können begrenzt werden.

Im AdGuard-Modus sucht DYNEX nur im WLAN nach PCs. Mobilfunk wird nicht begrenzt; die Proxy-Durchleitung bleibt dort aktiv. Ohne PC-Gaming wird kein Downloadlimit angewendet. **Pause** entfernt das Limit und lässt die Weiterleitung bestehen. Bei **Stoppen** fordert DYNEX AdGuard auf, den Proxy auszuschalten. Nimmt AdGuard die Anfrage nicht an, etwa nach einer Passwortänderung, oder beendet Android DYNEX hart, kann der weiter eingestellte Proxy die Internetverbindung blockieren. Dann den Proxy in AdGuard ausschalten oder DYNEX neu starten. Die Schnittstelle liefert keine dokumentierte Bestätigung für die Anwendung einer Einstellung.

## Betrieb ohne AdGuard

Im eigenständigen Modus benötigt DYNEX die einmalige Android-VPN-Freigabe. Ein bereits aktives anderes VPN blockiert diesen Modus. Der Tunnel läuft nur bei gemeldetem PC-Gaming und vorhandenem Downloadlimit. Bei Mobilfunk wartet DYNEX passiv auf ein WLAN-Ereignis und sucht nicht nach PCs.

Der WLAN-Status wird über Netzwerkereignisse und einen viersekündigen Heartbeat beobachtet; bei Mobilfunk ruht die periodische Suche. DYNEX verwendet dafür weder Wake-Locks noch GPS noch WLAN-Scans. Eine konkrete Akkuersparnis wird nicht zugesichert. Benachrichtigungen zeigen den tatsächlichen Lauf- und Limitstatus; ein laufender Proxy-Dienst allein bedeutet noch nicht, dass AdGuard bereits Daten durch ihn leitet.

Nach **Schutz einschalten** bleibt der Vordergrunddienst auch beim Verlassen der Oberfläche und Entfernen aus der Liste zuletzt geöffneter Apps aktiv. **Stoppen** beendet ihn ausdrücklich. Androids erzwungenes Beenden und herstellerspezifische Akku-Eingriffe können einen Dienst trotzdem beenden. Der Android-Autostart bleibt einstellbar; DYNEX fordert keine dauerhafte CPU-Wachhaltung an.

Die Übersicht zeigt die von verbundenen PCs übernommene Download-/Uploadkapazität in Mbit/s. Daraus berechnet DYNEX den Downloadanteil des Handys. Ein unbekannter Uploadwert bleibt **—**; das Handy erfindet keine eigene Leitungsmessung. Ohne laufenden Dienst sind Pause, Stoppen und Aktualisieren deaktiviert; ohne WLAN oder automatische PC-Verbindung ist auch die Suche deaktiviert. Speichern wird erst bei Änderungen aktiv, eine angeforderte Suche meldet sich sichtbar zurück.

## Prüfstatus und Grenzen

Die portable Limitberechnung und die öffentlichen VPN-Routen wurden separat geprüft; die 361 Core-Prüfungen enthalten auch den mobilen Netzwerkbereich. Die Proxy-Prüfung mit `--tcp-only` meldet 92 erfolgreiche Host-Prüfungen. UDP und TCP-Half-Close sind damit nicht abgedeckt: Schon die direkten Vergleichssockets ohne DYNEX-Proxy funktionierten auf diesem Windows-Testhost nicht zuverlässig.

Die Debug-APK ist gebaut und in einem isolierten Android-35-Emulator installiert. Die unabhängigen Laufzeitprüfungen, 131 Proxy-Prüfungen einschließlich IPv4/IPv6, UDP und TCP-Half-Close sowie drei Android/Core-Kommunikationsprüfungen bestanden. Mit der echten AdGuard-App 4.14.2 wurden die automatische Proxy-Konfiguration, echte Browserweiterleitung über WLAN und simulierten Mobilfunk sowie die Proxy-Abschaltung beim DYNEX-Stopp geprüft. Der Erststart-Assistent wurde auf dem kleinen Emulatorbildschirm geöffnet und durchlaufen. Ein physischer WLAN-/FRITZ!Box-/AdGuard-Test und eine Akkuverbrauchsmessung stehen aus; Emulatorprüfungen ersetzen diese nicht.

Für reproduzierbares WLAN benötigt der hier eingesetzte Emulator 37.2.12 das Startargument `-feature -WiFiPacketStream`: Sein standardmäßiger Netsim-WLAN-Dienst beendete den gRPC-Datenstrom bereits vor dem App-Start. Der alternative, lokal dokumentierte WLAN-Pfad stellte reguläre DHCP-Adressen und Routen wieder her. Das betrifft nur die Testumgebung, nicht das APK.

Eine Verbindung mit aktivem AdGuard ohne jede Freigabe ist nicht möglich. Siehe [AdGuard-Automatisierung](https://adguard.com/kb/de/adguard-for-android/solving-problems/tasker/) und [ausgehender Proxy](https://adguard.com/kb/adguard-for-android/solving-problems/outbound-proxy/).
