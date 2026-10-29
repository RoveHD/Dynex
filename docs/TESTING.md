# DYNEX · Prüfstatus

## Automatische Spielerkennung und vereinfachte Oberfläche, 1.0.26 (29.10.2026)

Die vollständige lokale Runtime-Suite bestand mit **238 Prüfungen ohne Szenarienfehler**. Der abschließende fokussierte Lauf bestand mit **37 Prüfungen** einschließlich der zusätzlich ergänzten Validierung ungültiger Alt-Modi. Geprüft werden Hintergrundspiele mit exaktem PID-/Startzeit-Paketnachweis, fehlende und abgelaufene Nachweise, fremde beziehungsweise verwaiste Identitäten sowie Freigabe beim globalen Abschalten. Protokolle: `artifacts/runtime-automatic-games.log` und `artifacts/runtime-automatic-games-focused.log`.

Desktop-Publish, vollständiger unsichtbarer WPF-Smoke und FeatureChecks bestanden. Die Prüfungen umfassen sieben Fenstergrößen, die drei verbleibenden Einstellungsbereiche, tatsächlich entfernte Expertenfelder, Erhalt ungespeicherter Eingaben und versteckter gespeicherter Werte, Geräte-Refresh mit Busy-/Fehler-/Erholungszuständen, ausdrücklichen LAN-Opt-in bei bestehenden privaten Gruppen, Paging, verschlüsselte Loopback-Telemetrie und Ping-Hover. Eine zusätzliche allgemeine Button-Geometrieprüfung reproduzierte eine abgeschnittene Diagnosezeile bei 900×650; nach der Layoutkorrektur besteht sie. Allgemein, Geräte und Übersicht wurden in der kleinsten Größe zusätzlich visuell geprüft.

Die abschließende Headerprüfung sichert mindestens acht Pixel Abstand zwischen Beschreibung und Inhalt in allen drei Einstellungsbereichen und sieben Größen. Der komplette Smoke und die FeatureChecks bestanden erneut; 900×650 und 1920×1080 wurden zusätzlich visuell kontrolliert.

Ergebnisse: `artifacts/qa-simplified-header-margin-final/` und `artifacts/qa-simplified-header-margin-features-final/`. Alle Profile und Netzdaten waren isoliert; es wurden keine produktiven Einstellungen, Firewallregeln oder Treiber verändert. Ein echter Router-/Spiel-Belastungstest ist dadurch nicht ersetzt. Frühere Abschnitte können inzwischen entfernte Bedienfelder beschreiben.

## Discord-Korrektur, 1.0.21 (21.10.2026)

Die neue Regression für kleine Videoframes mit nur einem RTP-Paket scheitert nachweisbar an der unveränderten 1.0.20-Regel (`artifacts/discord-repro-before-fix.log`). Nach der Korrektur bestehen **889 Shaper-Prüfungen**, einschließlich echtem optionalem Node-Pipe-Client. Die zusätzlichen Fälle prüfen kleine Frames in beiden Richtungen bei 15/30/60/144 fps, 14-fps-Frames mit verschlüsselten Header-Erweiterungsdaten, abweichende Audio-Zeitbasen, verlorene Marker, große Keyframes und Sequenzlücken. Nach Ablauf erhalten auch Duplikate keine alte Videofreigabe. Protokoll: `artifacts/discord-frame-fix-shaper-tests.log`.

Zusätzlich wurde der laufende native Discord-PTB-Prozess über eine frische echte Windows-UDP-Sockettabelle zugeordnet. Künstliche kleine RTP-Videopakete ergaben je **12 Videotreffer pro Richtung** mit dieser tatsächlichen Prozessidentität; ausgehende Markierung, ECN und Transportbytes wurden geprüft. Dieser Test sendet keine Netzwerkpakete und öffnet keinen Kernel-Filter. Er ist kein Mitschnitt eines realen Videoanrufs. Protokoll: `artifacts/discord-owned-probe/result.log`. Der Anti-Cheat-API-Guard bestand.

Der Desktop-Build bestand ohne Warnungen/Fehler. Die gezielte Runtime-Prüfung `--general-priority-only` bestand **10 Checks, 0 fehlgeschlagene Szenarien**, einschließlich Helfer-/Desktop-Videopaketzähler sowie Abschaltung und Wiederaufnahme. Protokoll: `artifacts/discord-fix-desktop-runtime.log`. CI-Abnahme und veröffentlichte Artefakte werden zusätzlich an den konkreten Release-Commit gebunden. Eine Veröffentlichung bestätigt keine Installation auf Nutzergeräten oder einen echten Videoanruf.

## Erweiterte Videoerkennung, 1.0.20 (21.10.2026)

Die Shaper-Suite bestand **872 Prüfungen**, einschließlich echtem optionalem Node-Pipe-Client (`DYNEX_TEST_NODE_MEDIA=1`). Neu geprüft sind die konkreten Medienhostregeln für die zusätzlichen Streamingdienste über TLS und QUIC v1/v2, IPv4/IPv6, falsche Domain-Endungen und Nicht-Medienhosts, vertauschte/überlappende TCP-Handshake-Segmente sowie aktive Verbindungen über mehr als 30 Minuten mit weiterhin wirksamem Leerlaufablauf.

Die Discord-/Teams-Prüfungen verwenden synthetische RTP-/Kontrollpakete und isolierte Prozesszuordnungen: getrennte Sprach-/Video-SSRCs und Payload-Types, Frame-Probation, Sequenz-/Zeitstempelüberlauf, Duplikate/Reordering, Prozesswechsel, fremde Programme, dokumentierte Teams-Quellportgrenzen, Cachegrenzen/Ablauf und 2.500 zufällige oder beschädigte Header. AF41, ECN und unveränderte verschlüsselte Transportbytes wurden am Klassifizierer-/Markierungspfad geprüft.

Der Desktop-Release-Build bestand ohne Warnungen/Fehler, ebenso der Anti-Cheat-API-Guard. Die vollständige Runtime-Suite bestand **226 Prüfungen, 0 fehlgeschlagene Szenarien**, einschließlich Videoklassifizierung über den tatsächlichen Helfer-/Desktop-Statuspfad ohne Spiel und ohne Bandbreitenlimit. Protokolle: `artifacts/realtime-video-shaper-tests.log`, `artifacts/video-recognition-desktop-runtime.log`. Die tatsächlichen Lieferhostquellen und die verbleibenden Erkennungslücken sind unter [Videoerkennung](video-detection.md) dokumentiert. Keine echte Discord-/Teams-Konferenz, vollständige Abnahme sämtlicher Anbieter-CDNs, Kernel-/Routerlastprüfung oder Installation auf Nutzergeräten. CI-Abnahme und veröffentlichte Artefakte werden zusätzlich an den konkreten Release-Commit gebunden.

## ELFIX-Medienmeldung, 1.0.19 (21.10.2026)

Die Shaper-Suite bestand 567 Prüfungen einschließlich 119 neuer Medienprüfungen und echtem optionalem Node-Pipe-Client (`DYNEX_TEST_NODE_MEDIA=1`). Die Prüfungen binden Medienhinweise an Benutzer, ELFIX-Programmdatei, tatsächliche Prozessstartzeit und den genauen Server-Endpunkt; ungültige Nachrichten, Überflutung, Ablauf, fremde Prozesse und Verbindungsabbruch geben keine Freigabe. Der Anti-Cheat-API-Guard bestand.

Der gekoppelte lokale Test mit Electron 44.4.3 verwendet echte HTTPS-Antworten, den unveränderten Windows-Prozessprüfer und tatsächliche Betriebssystem-Socketzuordnung. Sowohl eingebetteter Browserabruf als auch `spielerNetz.medienAbruf` des eigenen ELFIX-Players wurden geprüft. Die zugeordnete Verbindung ergab AF41 an einem Testpaket bei erhaltenem ECN; ein fremder Browser erbte keine Markierung, und Pipe-Abbruch löschte die Freigabe. Auf ELFIX-Seite bestanden 41 Senderprüfungen, Sicherheits-/Medien-CORS-Prüfungen, Player-Regressionsprüfungen und Lint.

Dieser Test öffnet keinen Kernel-Filter. Ein echter erhöhter Helfer, physischer Router und Last aus zwei Geräten wurden damit nicht geprüft. CI-Abnahme und veröffentlichte Artefakte werden zusätzlich an den konkreten Release-Commit gebunden. Details: [ELFIX-Integration](elfix-integration.md).

## Reine Priorisierung, 1.0.16 (20.10.2026)

Lokal bestanden: Shaper 331 Checks, Bulk-Kompatibilität 61, alte Programmregeln 53, Runtime 227, gezielte Runtime-Priorisierung 14, Downloadaktivität ohne Limits 23, Spielverkehr/Sitzung 24 und DeviceRuntime 10. Dazu Status-/Sitzungsanzeige und Bericht 37, Setup 127 und Verbrauchsverlauf. Die Paketprüfungen bestätigen CS4/EF, Headerprüfsumme, ECN/VLAN/IPv6-Flowlabel, unveränderte Transportheader/Nutzdaten und unmittelbare Weiterleitung trotz alter globaler und anwendungsbezogener Caps. Der aktive Paketpfad enthält keine Pacing-Warteschlangen.

MobilePolicy 65, MobileRoutes 53 und MobileProxy TCP 123 Checks bestanden. Der vollständige lokale Core-LAN-Test erreicht einen Timeout bei „closed scope rejection“; der vollständige MobileProxy-Test hängt bereits am unabhängigen direkten UDP-Host-Basistest. Diese lokalen Läufe gelten nicht als vollständig bestanden. Die unveränderten vollständigen Tests bleiben in CI und Releaseprüfung verpflichtend; es gibt keinen Test-Skip oder Produktions-Fallback dafür.

Windows-Publish und Android-Release-Build erfolgreich. Android hat zwei bestehende GetAllNetworks-Veraltungswarnungen. Die Release-APK verwendet Version 1.0.16 / Code 10016; ihr Signaturfingerabdruck entspricht der vorherigen veröffentlichten APK. Die konkrete CI-Abnahme ist an den jeweiligen GitHub-Commit gebunden.

Die Featurefenster wurden auf einem unsichtbaren separaten Windows-Desktop mit isolierten Daten geprüft: Gerätealias/Ausschluss, Verbindungstest, Netzwerkprofile und Verbrauchsverlauf. Hauptfenster-Layout, sieben Fenstergrößen, fehlende Limitbedienung, QoS-Status ohne aktives Limit und Migration werden ebenfalls durch den Desktop-Smoke geprüft. Kein Test verändert produktive Firewallregeln, installiert einen Treiber oder übernimmt eine reale Routerkonfiguration.

Nicht durchgeführt: physischer Zwei-PC-/Handy-/Router-Belastungstest, Anti-Cheat-Praxistest oder Installation des Updates auf den Geräten des Nutzers. Frühere Prüfstände unterhalb dieses Abschnitts beschreiben historische Versionen und können entfernte Limitfunktionen betreffen.

## Navigation, Darstellung und unsichtbare Desktop-Tests (15.10.2026)

Die Oberfläche besitzt eine linke Navigation, getrennte Seiten für Übersicht und verbundene PCs, einen Zahnradknopf und sechs Einstellungsbereiche. Unter Darstellung lassen sich Klick-Sounds und kurze Seitenübergänge separat abschalten. Die Auswahl wird sofort als Vorschau angewendet und erst mit „Einstellungen speichern“ dauerhaft übernommen. Testprofile bleiben immer stumm.

Der Release-Build in `artifacts/qa-redesign-build` bestand mit null Warnungen und Fehlern. Der vollständige WPF-Smoke in `artifacts/qa-redesign-final` bestand auf einem separaten, nie aktivierten Windows-Desktop: sieben Fenstergrößen von 900×650 bis 2560×1440, Erhalt ungespeicherter Eingaben, Zahnradnavigation zum letzten Bereich, Speichern/Laden beider Feedback-Einstellungen, Abbruch schneller Animationen ohne verbleibende Verschiebung, PC-Seitenwechsel, beide Status-/Verbindungsbuttons, Programmsuche, Ping-Hover und verschlüsselte Peer-Telemetrie. Kleine und große Ansichten wurden zusätzlich als gerenderte Bilder kontrolliert. Peer-Messwerte sind ausdrücklich beschriftete Testdaten; kein physischer Zwei-PC- oder Treibertest. Die Lautsprecherwiedergabe wurde nicht geprüft.

`scripts/test-desktop.ps1 -ExePath <absolute-exe> -Mode Smoke -OutputDirectory <absolute-output>` startet diese Tests unsichtbar und protokolliert stdout/stderr. Es schaltet den Desktop nicht um und beendet bei Zeitüberschreitung ausschließlich den selbst gestarteten Prozess. `-Mode NegativeStartup` prüfte zusätzlich eine ungültige Testinstanz-GUID: erwarteter Exitcode 1 und Fehlerprotokoll, kein Dialog. Protokoll: `artifacts/qa-redesign-negative`. Dafür war Windows-Zugriff außerhalb der Sandbox nötig; ein verweigerter Desktopzugriff wird als Fehler gemeldet.

## Automatische PC-Verbindung, Firewall und Startfehler (15.10.2026)

Der Netzwerkfix ergänzt begrenzte Unicast-Erkennung im privaten lokalen Netz, sofort gespeicherte Moduswechsel, getrennte Ping-/DYNEX-Diagnose sowie gezielte Firewallregeln für beide UDP-Richtungen. Eine reproduzierte Zugriffssperre auf einen benannten Start-Mutex wird ohne unnötige Schreibrechte behandelt; Datei-Startfehler enthalten den betroffenen Pfad und erhalten die vorhandenen Daten.

Geprüft: Core-Suite im eingefrorenen Paketstand 280/280 bestanden, einschließlich UDP-Regressionen zu Port-unreachable und doppelter Geräteidentität. Setup-Suite 127 Prüfungen erfolgreich, inklusive Upgrade von einer alten Inboundregel auf beide Regeln, idempotenter Entfernung und unveränderten fremden Regeln. Beide Firewallregel-Konfigurationen wurden zusätzlich an echten, **nicht registrierten** Windows-COM-Objekten geprüft; keine Live-Regeln oder Treiber verändert. Desktop-Release-Build ohne Warnungen/Fehler.

`Dynex.Runtime.Tests --network-only`: 14 Prüfungen bestanden (Moduswechsel, Erhalt fremder Einstellungen, ungültige Ziele, verweigerter Speichervorgang, Ping-Auswahl/-Abbruch und Startdatei-Zugriffsfehler). Im vorherigen vollen Lauf bestanden 159 Prüfungen; ein neuer Fall scheiterte an privaten LAN-Adressen im isolierten Loopbackprofil. Die Testisolierung wurde korrigiert und der betroffene Bereich danach erfolgreich gezielt geprüft; dieser Eintrag behauptet keinen erneut vollständig grünen Runtime-Lauf.

`DYNEX.exe --smoke-test <Ausgabeordner> --network-only` bestand auf einem separaten, unsichtbaren Windows-Desktop. Tatsächliche Checkbox-/Button-Aktionen, Speichern der direkten Gegenadresse, Fehlerfeedback und Erhalt ungespeicherter anderer Eingaben wurden geprüft. Netzwerkansicht bei 900×650, 1440×940 und 1920×1080 gerendert; im kleinsten Fenster wurden alle Felder nach einer Größenkorrektur sichtbar bestätigt. Protokolle/Bilder: `artifacts/network-focused-tests.log`, `artifacts/qa-network-auto-connect`. Die beiden physischen Nutzer-PCs, deren Repeater und eine echte Installer-Firewalleinrichtung wurden hier nicht Ende zu Ende geprüft.

## Suchbare Programmauswahl und hinzugefügte Programme (15.10.2026)

Der Desktop-Release-Build bestand ohne Warnungen oder Fehler. Der gezielte isolierte WPF-Test `DYNEX.exe --smoke-test <Ausgabeordner> --process-picker-only` prüfte Suche nach Namensbestandteilen ohne Beachtung der Groß-/Kleinschreibung, fehlende Treffer, Leeren und Aktualisieren der Suche, explizite Auswahl, Duplikatschutz, Hinzufügen/Entfernen, Leerzustand und Speichern/erneutes Laden einer nichtleeren sowie leeren Liste. Navigation erhält ungespeicherte Einträge. Die Spiele-Seite wurde bei 900×650 und 1440×940 gerendert und visuell geprüft; Protokolle und Bilder: `artifacts/qa-process-list-search-focused`. Zusätzlich lief die vorhandene GameDetection-Suite erfolgreich. Die UI-Prüfung lief auf einem separaten Windows-Desktop mit isoliertem Profil; kein Spiel wurde gestartet.

Der ebenfalls gestartete vollständige Oberflächentest brach vorher auf Einstellungen/Allgemein bei 1920×1080 mit 3,40 Einheiten vertikalem Scrollbedarf ab (`artifacts/qa-process-list-search/result.txt`). Der gezielte Test ersetzt keine vollständige Abnahme der anderen Einstellungsseiten.

## Verbindungsdiagnose, Limitgrund, Update-Absicherung (1.0.6)

Am 15.10.2026 in einer Linux-Umgebung (.NET-SDK 10, Ziel net9.0) ausgeführt und bestanden: **180 Core-Prüfungen** (davon 25 neu), **111 Update-Prüfungen** (davon 17 neu), 59 Adaptive- und 19 Speedtest-Prüfungen. Neu geprüft sind die Ursachenauswahl mit nächstem Schritt, signierte Versionsangabe im AutoLAN-Handshake (inklusive Altformat ohne Version und Abweisung ungültiger Angaben), echte Loopback-Verbindung zweier AutoLAN-Dienste mit Versionsabweichung, Ablauf und automatischer Wiederverbindung nach Neustart des Gegenübers, Zählung zukünftiger Protokollversionen, Limitgrund-Texte sowie Signatur-Richtlinie (gleicher Herausgeber, unsigniert, fremder Herausgeber, ungültige Signatur), erneute Prüfung direkt vor der Übergabe und das Überspringen einer zurückgerollten Version.

Der Desktop und die Windows-Testprojekte (Runtime, Setup, UserCleanup) wurden mit `EnableWindowsTargeting` ohne Warnungen oder Fehler kompiliert, aber **nicht ausgeführt** (Windows erforderlich). Beide NSIS-Skripte wurden mit makensis 3.09 und Testinhalt erzeugt; der Signaturhaken lief dabei für Setup, Uninstaller und Bereinigung. `scripts/build.ps1` wurde mit PowerShell 7.4 syntaktisch geprüft. Nicht ausgeführt: WinVerifyTrust gegen echte signierte Dateien, Signieren mit einem echten Zertifikat, Rollback-Pfad eines echten Setups und der Zwei-PC-Test unten.

### Funktionen aus der Featureliste (1.0.6)

Zusätzlich geprüft: Benachrichtigungslogik (Übergänge, Ratenbegrenzung), Pause als Limitgrund, Bufferbloat-Bewertung und Median, Leerlauf-/Lastping im Speedtest, gewichtete Budgetanteile inklusive Rückfall auf gleiche Anteile mit älteren PCs, Spielsitzungs-Zusammenfassung, UPnP-Routerzähler (SSDP-Herkunft, Hostbindung, DTD-Abweisung, 32-Bit-Überlauf, End-to-End gegen einen simulierten Router) sowie Beta-Kanal (Vorabversionen, Entwürfe, fehlerhafte Einträge). Launcher-Erkennung (Ubisoft/EA/Battle.net-Registrierungen, Xbox `MicrosoftGame.config` inklusive DTD-Abweisung) läuft in den Windows-Tests.

**GitHub Actions (Windows-Runner):** Runtime-, Setup-, UserCleanup-, GameDetection-, Bulk- und Shaper-Tests sowie der unsignierte Installer-Build laufen bei jedem Push; Lauf 7 (15.10.2026) war vollständig grün. Ein zeitabhängiger Updater-Test (Wettlauf zwischen Hintergrund- und manueller Prüfung) wurde dabei gefunden und behoben. Traffic- und Live-Tests benötigen echten Filter bzw. Router und bleiben manuell.

Nicht automatisch prüfbar und deshalb offen: Infobereich-Benachrichtigungen und Assistent auf echtem Desktop, UPnP gegen echte Router, Launcher-Erkennung mit echten Installationen.

**Upload-Begrenzung:** Die Klassifizierung (nur große Uploads identifizierter Nicht-Spiel-Programme; ACKs, Spiele, Sprachchat, unbekannte Eigentümer frei) und eine eigene Upload-Warteschlange sind mit simuliertem Paketgerät geprüft: Spielpakete und eingehende Pakete passieren sofort, während ein Upload gesättigt ist. Mit echtem NDIS-Filter noch nicht geprüft (Abnahmeschritt 19). **Bewusst nicht umgesetzt:** eine englische Oberfläche (alle Texte müssten in Ressourcen ausgelagert werden; eigenes Paket).

### Abnahme auf zwei echten PCs (offen)

Auf PC 1 und PC 2 jeweils DYNEX 1.0.6 installieren, beide im selben WLAN/LAN mit Windows-Profil **Privat**. Nach jedem Schritt „Diagnose exportieren“ sichern.

| # | Schritt | Erwartung |
| --- | --- | --- |
| 1 | Beide starten (AutoLAN an) | Innerhalb von 10 s je 2 Geräte unter **Verbundene PCs**, keine Problemmeldung |
| 2 | DYNEX auf PC 2 beenden | Auf PC 1 nach ≤ 10 s „„PC 2“ antwortet seit … nicht mehr“ mit nächstem Schritt |
| 3 | DYNEX auf PC 2 wieder starten | Verbindung ohne Eingriff wieder da, Meldung verschwindet |
| 4 | PC 2 in Standby, nach 2 min aufwecken | Verbindung ≤ 30 s nach Anmeldung wieder da |
| 5 | PC 2 auf anderes WLAN/Gastnetz und zurück | Während der Trennung „antwortet nicht mehr“ bzw. „anderes Netz“; danach automatische Wiederverbindung |
| 6 | Netzwerkprofil PC 2 auf **Öffentlich** | PC 2 zeigt „nicht als privat eingestuft“ + Schritt „Profil auf Privat“ |
| 7 | AutoLAN auf PC 2 aus, PC 1 an | „anderer Verbindungsmodus“ |
| 8 | Code auf PC 1 kopieren, auf PC 2 importieren | Verbindung über private Gruppe, auch bei blockiertem Multicast |
| 9 | PC 2 mit 1.0.5, PC 1 mit 1.0.6 | PC 1 meldet „„PC 2“ vor 1.0.6 … aktualisieren“ |
| 10 | Spiel auf PC 2 im Fokus, Download auf PC 1 | PC 1: Downloadbudget mit Grund „Gaming auf PC 2 (…) erkannt“, Tooltip mit aktueller Geschwindigkeit |
| 11 | Update 1.0.6 → Folgeversion über **Jetzt prüfen** | Signatur akzeptiert, Installation beim nächsten Start, Version erhöht |
| 12 | Folgeversion mit absichtlich defektem Helper | Setup stellt 1.0.6 wieder her, Hinweis beim Start, kein erneuter Auto-Download |
| 13 | Tray → 30 min pausieren, während PC 2 spielt | Limitgrund „Von dir pausiert bis …“, Downloads frei; „Pause beenden“ stellt das Limit wieder her |
| 14 | PC 1 Priorität Hoch, PC 2 Normal, Spiel auf PC 2 | Limit PC 1 ≈ 2/3, PC 2 ≈ 1/3 des Budgets |
| 15 | Speedtest bei laufendem Download auf einem Handy | Bufferbloat-Bewertung und Empfehlung erscheinen |
| 16 | Router mit UPnP-Status (FRITZ!Box) | Download-Karte „Ganzes Heimnetz“; Tooltip mit Geräten ohne DYNEX |
| 17 | Spiel aus Ubisoft/EA/Battle.net/Xbox starten | Spiel wird erkannt, Launcher allein nicht |
| 18 | 5 min spielen, beenden | Benachrichtigung „Spielsitzung beendet“ mit Dauer und Ping |
| 20 | Steam-Update auf PC 2, Spiel auf PC 1 | Geräteliste zeigt bei PC 2 „steam“ in „Lädt gerade“ |
| 21 | Download auf PC 2 starten, während PC 1 spielt (ohne Limit, z. B. Pause) | Ping-Alarm auf PC 1 nennt PC 2 als Ursache |
| 22 | Planung: Nachtfenster auf die aktuelle Stunde setzen | Limitgrund „Zeitplan … ohne Limits“, Downloads frei |
| 23 | Einstellungen → Analyse beim Spielen öffnen | Top-Programme und Router-Assistent passend zum Router |
| 24 | Einen PC ins Gast-WLAN, dann „Verbindung prüfen“ | Hinweis „Gastnetz“ mit nächstem Schritt |
| 19 | Spiel im Fokus, gleichzeitig großer OneDrive-/Google-Drive-Upload | Upload-Karte „Andere Uploads ≤ … Mbps“, Ping bleibt stabil, Spiel und Discord unbeeinträchtigt; parallel laufender Download wird nicht langsamer |


## PC-Kopplung und Verbindungsdiagnose (1.0.5)

Am 14.10.2026 bestanden **156 Core- und 117 Runtime-Prüfungen**. Der Desktop-Release-Build endete ohne Warnungen oder Fehler. Geprüft wurden der bisher fehlerhafte Export/Import zwischen zwei zunächst automatischen Instanzen, dauerhafter Protokollwechsel, Erhalt der Identität, Schreibfehler, begrenzte private Adresslisten und DYNEX1-Kompatibilität. Einseitig konfigurierte direkte UDP-Verbindungen erhalten nun auch die Rückrichtung. Die Transportprüfungen verwenden echte verschlüsselte UDP-Kommunikation auf Loopback, einschließlich Routenablauf, ungültiger Authentifizierung, Replay-Abwehr und authentifizierter Zeitabweichung.

Die neuen Diagnoseprüfungen unterscheiden eigene Pakete, fremde Protokollmarkierungen, gültige Handshakes und abgewiesene Netzwerkbereiche. Ein fremder Modus oder ein empfangenes Paket erzeugt keine vertrauenswürdige Geräteanzeige. Fehlende Nachrichten werden nicht als bewiesener Firewallfehler ausgegeben. Logs: `artifacts/core-tests-1.0.5.log`, `artifacts/runtime-pairing-tests-final.log` und `artifacts/desktop-build-1.0.5.log`.

Ein fehlgeschlagener Entwicklungscheck hatte zunächst wegen einer unbehandelten Ausnahme einen Windows-Fehlerdialog geöffnet. Der Testprozess wurde beendet. Der Core-Testeinstieg fängt Fehler jetzt ab, schreibt sie ins Protokoll und endet mit Fehlercode 1; Fehlerdialoge sind zusätzlich ausschließlich für diesen Testprozess unterdrückt. Der abschließende Testlauf über die DLL endete erfolgreich ohne solche Fenster. Einzelne UDP-Testpakete wurden auf neuen Sockets nicht zuverlässig zugestellt; die neuen Transportfixtures senden deshalb wie der echte Dienst periodisch bis zu einer begrenzten Beobachtungsfrist. Authentifizierungs- und Replay-Anforderungen bleiben unverändert.

Kein neuer sichtbarer WPF-Smoke, keine Installation oder Änderung von Windows-Netzwerkeinstellungen und kein physischer Zwei-PC-Test auf KING-PC wurden ausgeführt. Das anfängliche AutoLAN-Problem auf den entfernten PCs ist damit noch nicht als behoben bestätigt; die neue Diagnose erfasst die dafür bislang fehlenden Beobachtungen.

## Vollständige Deinstallation und nachträgliche Bereinigung (1.0.4)

Die 86 Setupprüfungen und 12 Prüfungen der Benutzerbereinigung bestanden. Getestet sind neu angelegte, vorbestehende, gemeinsam genutzte und alte unbekannte Filter; Upgrade-Herkunft, MSI-Abbruch/Fehler, wiederholte Bereinigung, gesperrte Dateien, fremde Verknüpfungen sowie Junctions mit erhaltenem Ziel. Die Tests der Systemänderungen verwenden injizierte Abhängigkeiten und deinstallieren keinen echten Treiber. Eine reale schreibgeschützte Prüfung bestätigte die Produktmetadaten des installierten MSI und die über WMI gelesene Bootidentität. Der Desktopbuild bestand ohne Warnungen oder Fehler.

Zusätzlich bestanden alle 98 Runtimeprüfungen mit isolierten Profilen und einem Ersatz für das native Paketgerät; darunter Fokuswechsel, Peer-Ablauf, Freigabe beim Beenden und Messfreigabe vor einem Downloadlimit. Damit sind für 1.0.4 insgesamt 196 fokussierte Prüfungen bestanden. Runtimeprotokoll: `artifacts/runtime-tests-1.0.4.log`.

Beide NSIS-Pakete wurden erzeugt. Das gebaute Bereinigungsprogramm und die verpackte Cleanup-CLI wurden zusätzlich im sicheren Abbruchfall einer noch installierten App ausgeführt: beide meldeten 5203 vor Erhöhung und Bereinigung. Drei ungültige CLI-Aufrufe wurden mit Code 2 zurückgewiesen. Prüfsummen von installiertem Programm und Einstellungen sowie Autostart, Filterdienst und Adapterbindungen blieben dabei unverändert. Eine Lesesperre auf der vorhandenen App verhindert, dass der Test versehentlich in eine echte Bereinigung übergeht. Protokoll: `artifacts/qa-cleanup-1.0.4/result.json`. NSIS liest die deutschen Quelldialoge ausdrücklich als UTF-8.

Neustartpflicht wird nicht aus verschwundenen Registryeinträgen abgeleitet: Im selben Windows-Start bleibt MSI-Code 3010 offen. Eine geänderte `Win32_OperatingSystem.LastBootUpTime` und bestätigte Filterabwesenheit erlauben nach dem Neustart wieder eine Neuinstallation. Fehlende WMI-Daten, umgeleitete Benutzerordner, unbekannte MSI-Identitäten und laufende Helper werden ausdrücklich diagnostiziert. Im Normalfall entfernt die letzte registrierte DYNEX-Installation ihren eigenen Filter; vorhandene fremde Filter werden erhalten. Alte Installationen ohne Herkunftsnachweis erfordern eine ausdrückliche Entscheidung.

Der tatsächliche MSI-Ausbau einschließlich möglicher Netzwerkunterbrechung oder Neustart wurde nicht auf dem Arbeits-PC ausgeführt. Eine reale vollständige Installieren–Deinstallieren-Abnahme auf einem separaten Windows-Testsystem steht aus. Andere Programme, die den Filter nachträglich mitverwenden, können nicht vollständig automatisch inventarisiert werden. Bei Neustartpflicht bleibt bis zur nächsten bestätigten Bereinigung oder Einrichtung ein geschützter Wiederaufnahmebeleg bestehen.

## Paketfilter nur während Gaming (1.0.3)

Am 14.10.2026 bestanden 98 Runtime- und 190 Shaper-Prüfungen. Die neuen Integrationsszenarien verbinden die echte Runtime, den Desktop-Controller und die Helper-Kommandoschleife über echte Named Pipes; nur das native Paketgerät wird durch ein Testgerät ersetzt. Ohne Gaming, bei Discord allein und mit ausschließlich Hintergrundspielen wird keine Capture-Sitzung angefordert. Lokaler Fokus aktiviert QoS und das zulässige Limit; Alt-Tab gibt das Gerät tatsächlich frei. Ein fokussierter Peer hält den Schutz aktiv, ein Fokusverlust oder abgelaufener Peer gibt ihn frei. Erneutes Gaming öffnet eine neue Sitzung auf dem vorhandenen passiven Helper. Ohne erfolgreiche Messung ist nur QoS während Gaming erlaubt, kein Downloadlimit.

Zusätzlich geprüft: kein wiederholter Startversuch nach gescheitertem Helperstart, erneute Bedarfsprüfung nach dem Start, ausdrückliche befehlsbezogene Freigabebestätigung sowie Ablehnung veralteter Bestätigungen und sichtbare Freigabefehler. Es wurde in diesen Tests kein Kernel-Filter geöffnet und keine echte UAC-Abfrage ausgelöst. Logs: `artifacts/runtime-tests-1.0.3.log` und `artifacts/shaper-tests-1.0.3.log`.

Der isolierte WPF-Smoke unter `artifacts/qa-ui-1.0.3` bestand für Übersicht und alle Einstellungen bei sieben Fenstergrößen von 900×650 bis 2560×1440. Die neuen Texte wurden bei 900×650 visuell geprüft. Eine zusätzliche Fixture unterscheidet ausdrücklich „Netzwerkfilter inaktiv“ von einer unbestätigten Freigabe; die Darstellung ist kein nativer Freigabenachweis. Ein neuer physischer Mehr-PC- oder Gaming-/Download-Lasttest wurde nicht ausgeführt.

## Integrierte Einrichtung, AutoLAN und Adapterwechsel (1.0.2)

Am 14.10.2026 bestanden 438 automatisierte Prüfungen: 40 Setup-, 141 Core-, 86 Runtime- und 171 Shaper-Checks. Die Setup-Prüfungen verwenden injizierte Abhängigkeiten und prüfen vorhandene Komponenten, Fehler, Abbruch und Neustartbedarf sowie die eng begrenzte Firewallregel. Sie installieren keinen Treiber und ändern keine Windows-Firewall. Die echte schreibgeschützte COM-Abfrage wurde zusätzlich geprüft: Eine fehlende Regel wird von .NET als `FileNotFoundException` mit HRESULT `0x80070002` geliefert und korrekt als noch nicht eingerichtet behandelt. Andere Fehler bleiben sichtbar. AutoLAN prüft Profile, verschlüsselte UDP-Loopback-Verbindungen und die Erholung nach vorübergehenden Netzwerkfehlern. Eine reale Verbindung zwischen zwei physischen PCs ist damit noch nicht nachgewiesen.

Die Shaper-Prüfungen führen die echte Dispose-Logik mit simulierten nativen IOCTL-Antworten aus: Fehler 87 bei einem alten Adapterhandle gilt nur dann als freigegeben, wenn eine vollständige neue Adapterliste dessen Verschwinden bestätigt. Vorhandene Handles, unvollständige Listen und andere Fehler bleiben Fehler. Ein neu erschienener Adapter wird dabei nicht verändert. Ein echter Windows-Adapterwechsel wurde nicht ausgelöst.

Der Desktop-Release-Build bestand ohne Warnungen oder Fehler. Der WPF-Smoke unter `artifacts/qa-ui-1.0.2` bestand für Übersicht und alle Einstellungsseiten bei sieben Größen von 900×650 bis 2560×1440. Die neuen Netzwerkfehler-Ansichten wurden bei 900×650 visuell kontrolliert: vollständiger Hinweis, Navigation zu den Netzwerkeinstellungen und Windows-Netzwerkknopf passen ohne Seitenscrollen. Die Runtime-Suite prüft den konkreten Netzwerk-Wartegrund statt der irreführenden Spielpausenmeldung. Protokolle: `artifacts/runtime-tests-1.0.2.log` und `artifacts/qa-ui-1.0.2/pages-result.txt`.

## Downloadlimit nur bei Spiel im Fokus (1.0.1)

Am 14.10.2026 bestanden 85 Runtime- und 162 Spielerkennungsprüfungen. Abgedeckt sind Fokusverlust und Rückkehr, Auswahl des tatsächlich fokussierten von mehreren offenen Spielen, unbekannte Fenster, PID-/Prozessname-/Startzeit-Abgleich, Ausschluss von Wallpaper Engine und Launchern sowie die bisherige Einstellung „Gaming immer an“. Ein manuelles Maximum greift nur bei lokalem oder entferntem Gaming im Fokus. Ein über echtes verschlüsseltes UDP-Loopback verbundenes Testgerät hält das Limit bei lokalem Alt-Tab aktiv und gibt es bei eigenem Fokusverlust frei. Hintergrundspiele blockieren weiterhin den Speedtest; ohne erfolgreiche Messung bleibt auch bei Fokus jedes Downloadlimit gesperrt. Zwei bisherige Prüfungen warten jetzt auf den vollständigen Abschluss der Speedtest-Wiederherstellung statt auf einen vorübergehenden Zwischenstatus.

Die Spiele und Fokuswechsel sind injizierte Testdaten. Eine echte, ausschließlich lesende Windows-Vordergrundabfrage wurde zusätzlich ausgeführt. Der Runtime-Test beobachtete Fokusgewinn nach 2021 ms und Fokusverlust nach 1984 ms; das sind einzelne Messungen des vorhandenen Zwei-Sekunden-Zyklus, keine garantierte Reaktionszeit. Ping-Timeouts, Helper und LAN-Übertragung können die Übernahme verlängern. Kein neuer physischer Mehr-PC- oder echter Spiel-Alt-Tab-Lasttest.

Der Desktop-Release-Build bestand ohne Warnungen oder Fehler. Der WPF-Smoke unter `artifacts/qa-ui-1.0.1` bestand für Übersicht und alle Einstellungsseiten bei sieben Fenstergrößen von 900×650 bis 2560×1440, einschließlich Ping-Hover, Prozessauswahl, Speichern, Update-Checkbox, verschlüsselter Peer-Anzeige und sauberem Beenden. Die geänderten Einstellungsseiten wurden bei 900×650 visuell geprüft. Weitere Protokolle: `artifacts/runtime-tests-1.0.1.log` und `artifacts/game-detection-tests-1.0.1.log`.

## Getrennte Seiten und Speedtest-Freigabe (1.0.0)

Am 14.10.2026 bestanden 71 Runtime- und 133 Core-Prüfungen. Der Speedtest wartet auf erfolgreiche Netzwerk-/Spielstatusprüfung und spielfreie lokale sowie verbundene DYNEX-PCs. Abgedeckt sind manuelle Ablehnung bei Gaming, lokaler und entfernter Spielbeginn während der Messung, ein Spielstart zwischen Prüfung und tatsächlichem Messbeginn, Wartezustand/Stop, genau ein verbrauchter Automatikversuch sowie fehlgeschlagene Speicherung. Ohne erfolgreich gespeicherte Kapazität wird auch ein manuelles Limit nicht freigegeben. Die beiden Peer-Verfahren veröffentlichen dann keinen Kapazitätsvorschlag. Die Prüfungen verwenden simulierte Downloads/Spielprozesse, isolierte Profile und verschlüsselte UDP-Verbindungen auf Loopback; keine neue physische Mehr-PC- oder Filterabnahme.

Der WPF-Smoke unter `artifacts/qa-layout-final-1.0.0` bestand: Übersicht und alle vier Einstellungsseiten passen bei 900×650, 1000×720, 1366×768, 1440×940, 1920×650, 1920×1080 und 2560×1440 ohne Seitenscrollen. Zusätzlich geprüft wurden vollständige Pingdetails und der nicht abgeschnittene Diagrammrand, Erhalt ungespeicherter Eingaben beim Wechseln, drei sichtbare Gerätezeilen und Navigation durch 13 Testgeräte ohne Zurückspringen beim Messwert-Update. Prozessauswahl, tatsächlicher Speichern-Knopf, Update-Checkbox, Ping-Hover, verschlüsselte Peer-Telemetrie, Diagnoseexport und sauberes Beenden sind weiterhin enthalten. Die gerenderten Ansichten wurden visuell kontrolliert, einschließlich kleiner und breiter/flacher Fenster. Die Größen sind WPF-Einheiten; ein zweiter physischer Monitor mit anderer DPI wurde nicht getestet.

Die UI-Prüfung verwendet eine ausdrücklich gespeicherte Kapazitätsfixture mit Zeitstempel 01.01.1970, damit sie keinen echten Speedtest auslöst. Pingwerte stammen aus realen Messungen; Peer-Durchsatzbilder sind als Testdaten beschriftet. Die Messpflicht selbst wird separat durch die Runtime-Prüfungen getestet. Ergebnisprotokolle: `artifacts/runtime-tests-1.0.0.log`, `artifacts/core-tests-1.0.0.log` und `artifacts/qa-layout-final-1.0.0/pages-result.txt`.

Die folgenden Abschnitte halten frühere, getrennt ausgeführte Prüfungen fest.

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

## Hintergrundupdates und einmaliger Speedtest (0.5.0)

Die Update-Suite (`dotnet run --project tests/Dynex.Update.Tests -c Release`)
bestand 94 Prüfungen: Opt-in/Opt-out, manuelle Prüfung ohne Download, Versions- und
Assetprüfung, SHA-256, abgebrochene/beschädigte Downloads, vertrauenswürdige
Weiterleitungen, Cacheprüfung und einmaliger Handoff, gleichzeitige Aufrufe und
Gaming-Pause. HTTP und Pakete sind dabei simuliert. Die separate Option
`-- --live-check` bestand gegen das öffentliche GitHub-Release ohne Anmeldung;
dieser Livecheck liest nur Metadaten und installiert nichts.

Die Runtime-Suite bestand 44 Prüfungen, darunter sofortiges Speichern der
Update-Checkbox ohne Neustart von Worker/Peers, neue Standardwerte, dauerhaft
gespeicherter einmaliger Speedtestversuch, Neustarts nach Fehler/Abbruch sowie
weiterhin mögliche manuelle Messungen. Alte Einstellungsobjekte können den
Einmal-Marker nicht zurücksetzen.

Der WPF-Smoke-Test schaltet die tatsächliche Checkbox im isolierten Profil an und
aus und prüft die gespeicherten Werte. Ein Prozesscheck bestätigt, dass
`--update-idle` eine laufende Instanz nicht beendet und der Installations-Mutex
neue normale Starts verhindert. Der Produktionsfilter wird dabei nicht verändert.

Diese Version bringt keine neue physische Mehr-PC-, Discord- oder Anti-Cheat-Abnahme.
DYNEX-Installer bleiben unsigniert; SHA-256 ist keine Herausgebersignatur.

## Geräte, Programmregeln, Netzprofile und Verbrauch (lokaler Teststand 1.0.10)

Die fünf Erweiterungen und ihre Bedienwege sind in [desktop-features.md](desktop-features.md) dokumentiert. Die finalen Regressionen bestanden: 280 Core-, 298 Shaper-, 188 Runtime-, 39 Geräte-/Probe-, 10 Geräte-Runtime- und 47 Programmregel-Prüfungen. Die Runtime-Suite enthält 29 Prüfungen für Netzwerkprofile und Messwertzuordnung. Die Verbrauchstests bestanden ebenfalls, einschließlich DST/Tagesgrenzen, Messlücken, laufender Zeitblöcke, großer gespeicherter Werte und beschädigter Daten.

Die Prüfungen verwenden isolierte Profile, dynamische Loopback-Ports und simulierte Paketgeräte. Der echte Kernel-Netzwerkfilter wurde dabei nicht aktiviert. Der Feature-UI-Smoke bedient Hinzufügen/Speichern/Entfernen, Alias/Sperre, Profilnamen und Tages-/Wochenauswahl sowie Datumsauswahl mit Rückkehr zu Heute und rendert alle fünf neuen Fenster in Normal- und Mindestgröße auf einem unsichtbaren Windows-Desktop. Belege liegen lokal unter `artifacts/qa-features-dates-final` sowie in `artifacts/features-{core,shaper}-tests.log`, `artifacts/features-runtime-final.log` und `artifacts/features-devices-final.log`.

Noch offen bleiben eine physische Zwei-PC-Abnahme über die tatsächlich verwendeten Repeater/Router und die Durchsatzprüfung der Programmregeln am echten Filter. Verbrauchswerte sind Schätzungen mit ausgewiesenen Lücken; der lokale Test-Installer wird durch diese Prüfungen weder installiert noch veröffentlicht.

Der vollständige MainWindow-Smoke bestand ebenfalls (Exitcode 0): alle Seiten von 900×650 bis 2560×1440, Live-Pinganzeige, verschlüsselter Loopback-Peer-Verkehr, Ablauf/Trennung und Shutdown. Belege: artifacts/qa-main-features-complete/result.txt und pages-result.txt. Dabei wurde eine echte Überlappung am Diagrammrand in der kompakten Ansicht behoben; die kompakte Ansicht erhält nun ausreichend Zeichenhöhe.
