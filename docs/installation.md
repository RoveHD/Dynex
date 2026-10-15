# DYNEX 1.0.6 installieren und entfernen

Das Release-Setup `artifacts/DYNEX-Setup-1.0.6.exe` installiert DYNEX pro Windows-Benutzer unter `%LOCALAPPDATA%\Programs\DYNEX`. Es enthält die Desktop-App und den erforderlichen `Dynex.Shaper.exe`-Helper. Die App bleibt pro Benutzer installiert und benötigt keine separate .NET-Laufzeit. Für Netzwerkvoraussetzungen und deren Besitznachweis fordert das Setup bei Bedarf Administratorrechte an. Ab 1.0.6 kann der Build App, Helper, Setup, Uninstaller und Bereinigungsprogramm mit einem Authenticode-Zertifikat signieren ([Signieren](updates.md#signieren)); ob ein Release signiert ist, zeigt **Eigenschaften → Digitale Signaturen** der Setup-Datei. `artifacts/DYNEX-Cleanup-1.0.6.exe` bereinigt bereits deinstallierte ältere Versionen, ohne DYNEX neu zu installieren.

## Start im privaten LAN

Starte DYNEX auf den PCs im selben privaten Windows-Netzwerk. AutoLAN, Windows-Autostart und automatischer Schutz beim Spielen sind standardmäßig eingeschaltet; für den ersten Kontakt ist kein Gruppencode nötig. Eine neue Peer-Identität im privaten LAN wird beim ersten Kontakt automatisch vertraut. Optional kann weiterhin eine private gemeinsame Gruppe genutzt werden.

Der Speedtest startet automatisch genau einmal. Auch nach Fehler oder Abbruch gibt es beim nächsten Programmstart keinen automatischen Wiederholungsversuch. Danach lässt er sich über den Speedtest-Knopf manuell starten. Automatische GitHub-Updates lassen sich separat in den Einstellungen anhaken; [Details zum Ablauf](updates.md).

Der lokale Windows-PC-Name wird automatisch verwendet. Die Anschlusskapazität wird einmal gemessen und gespeichert. Ab 0.3.1 stimmen die PCs ihre unterschiedlichen Messwerte automatisch ab und teilen den kleinsten daraus errechneten Downloadtopf. Die Reserve je Gaming-PC beträgt automatisch `min(10 Mbit/s, 5 % der Kapazität)`; das Standard-Hintergrundbudget beträgt 65 %. Installiere die aktuelle Version auf jedem beteiligten PC und schalte jeweils die Downloadbegrenzung ein. Ein bereits eingerichteter Netzwerkfilter muss beim App-Update nicht erneut installiert werden.

## Netzwerkeinrichtung im Setup

Das DYNEX-Setup prüft den Netzwerkfilter und eine eigene Firewallfreigabe. Fehlt der Filter, lädt es das offizielle Windows Packet Filter x64 MSI direkt vom Hersteller in den Cache des installierenden Benutzers. Dateigröße, fester SHA-256 und Authenticode-Signatur werden vor Ausführung geprüft; die erhöhte Phase prüft erneut und hält die geprüfte Datei bis zum Installationsende gesperrt. Das MSI und der Kernel-Treiber werden nicht im DYNEX-Paket weiterverteilt.

Eine Windows-UAC-Bestätigung erlaubt die nötigen Systemänderungen. Der offizielle MSI-Installer läuft mit Fortschrittsanzeige ohne zusätzlichen Einrichtungsassistenten. Die Verbindung kann kurz unterbrochen werden. Bereits registrierte Filter werden übersprungen und nicht neu installiert. Ein nötiger Neustart wird als Ergebnis 3010 angezeigt; DYNEX erzwingt ihn nicht. Bei Abbruch, fehlendem Internet oder einem Fehler meldet das Setup eine unvollständige Einrichtung und einen Fehlercode. Details stehen unter `%LOCALAPPDATA%\Programs\DYNEX\setup-result.txt`; Setup erneut starten oder die Einrichtung in DYNEX verwenden.

Die eigene Firewallregel gilt nur für die installierte DYNEX.exe, eingehendes UDP 39274, das Windows-Profil Privat und Absender im lokalen Subnetz. Öffentliche und Domänennetze werden dadurch nicht freigegeben. DYNEX ändert das Netzwerkprofil nicht automatisch. Ist das Heimnetz in Windows noch öffentlich, zeigt die App den konkreten Grund und öffnet auf Wunsch die Windows-Netzwerkeinstellungen. Auf allen beteiligten PCs muss DYNEX laufen. Routerseitige Client-Isolation oder zusätzliche Firewallregeln können die Verbindung weiterhin verhindern.

Der Speedtest unterscheidet jetzt zwischen laufender Prüfung, nicht verfügbarer PC-Verbindung und einem tatsächlich offenen Spiel. Ohne erfolgreiche Messung werden keine Downloadlimits aktiviert. Ein Netzwerkfehler wird nicht als Spielpause bezeichnet.

## Wenn der zweite PC fehlt

Ab 1.0.6 nennt DYNEX bei einem Verbindungsproblem eine beobachtete Ursache und genau einen nächsten Schritt, in der Übersicht und unter **Einstellungen → Netzwerk**:

| Anzeige | Bedeutung | Nächster Schritt |
| --- | --- | --- |
| Netzwerk nicht privat | Keine Adresse in einem privaten Windows-Netzwerk | Windows-Netzwerk öffnen und das Profil auf beiden PCs auf **Privat** stellen |
| Firewallfreigabe fehlt | Die eigene DYNEX-Regel ist nicht vorhanden | DYNEX-Setup auf diesem PC erneut ausführen |
| Andere DYNEX-Version | Der andere PC meldet eine abweichende Version (ab 1.0.6 signiert im Handshake; ältere als „vor 1.0.6“) oder ein neueres Protokoll | Den älteren PC über **Updates → Jetzt prüfen** aktualisieren |
| Anderer Verbindungsmodus | Ein PC verbindet automatisch, der andere über eine private Gruppe | Auf beiden PCs denselben Modus wählen bzw. Code neu importieren |
| Zeitabweichung | Uhrzeiten weichen mehr als 30 s ab | Windows-Uhrzeit auf beiden PCs synchronisieren |
| Anderes Netz | Pakete kommen über eine nicht freigegebene Schnittstelle | Beide PCs in dasselbe Heimnetz (kein Gast-WLAN) |
| PC antwortet nicht mehr | Ein verbundener PC ist verschwunden (Standby, Netzwechsel, DYNEX beendet) | Den genannten PC aufwecken und DYNEX dort prüfen |
| Anderer PC antwortet nicht | Noch nie Pakete eines anderen PCs empfangen | DYNEX dort starten, sonst Gruppencode verwenden und Router-Client-Isolation abschalten |

Nach Standby baut DYNEX die LAN-Verbindung neu auf (frische signierte Sitzung). Kurzzeitige Empfangsfehler beim WLAN-Wechsel beenden die Verbindung nicht mehr dauerhaft; DYNEX versucht es mit wachsender Pause bis 5 s erneut. Ein anderer PC erscheint nach Rückkehr in dasselbe private Netz in der Regel innerhalb weniger Sekunden wieder.

Ab 1.0.5 zeigt **Netzwerk prüfen** unter Einstellungen → Netzwerk die beobachtete Verbindungslage. Der Mauszeiger über dem Hinweis zeigt Modus, lokale Adressen und gesendete/empfangene Pakete. Die Prüfung liest zusätzlich die DYNEX-Firewallfreigabe; sie verändert keine Netzwerkprofile oder Sperrregeln. Der Diagnoseexport enthält diese Werte und die geprüften privaten LAN-Adressen, jedoch keine Kopplungsschlüssel. Fehlende UDP-Nachrichten beweisen allein keine bestimmte Firewall- oder Routerursache.

Für die direkte Kopplung beide PCs aktualisieren. Auf dem ersten PC **Kopieren** wählen und den neu erzeugten Code auf dem zweiten PC **importieren**. Beide verwenden damit dieselbe private Gruppe. Der Code enthält neben dem Gruppenschlüssel nur geprüfte private lokale IPv4-Adressen des ersten PCs. Der zweite PC sendet direkt dorthin; der erste antwortet nur an erfolgreich authentifizierte, aktuelle Teilnehmer. Damit benötigt diese Verbindung kein Multicast. Der Code ist geheim zu halten.

Bis 1.0.4 schaltete nur der Import den Empfänger in die private Gruppe, während der Sender beim Kopieren im Automatikmodus blieb. Diese unterschiedlichen Protokolle konnten sich nicht verbinden. Nach dem Update den Code neu kopieren und importieren. Vorhandene DYNEX1-Codes bleiben gültig, enthalten aber keine Direktadressen. Um zur automatischen Verbindung zurückzukehren, auf allen beteiligten PCs **PCs im selben privaten Netzwerk automatisch verbinden** einschalten und speichern. Auch die direkte Verbindung benötigt erreichbare PCs und eine passende Firewallfreigabe; eine Router-Geräteisolation wird nicht umgangen.

## Wirkungsbereich

Der Netzwerkfilter wird nur aktiv, wenn auf diesem oder einem verbundenen DYNEX-PC ein erkanntes Spiel im Vordergrund ist. Ohne Gaming gibt DYNEX auch die Sprachchat-Priorisierung frei. Die App bleibt zur Spiel- und PC-Erkennung geöffnet; Sprachchat allein aktiviert keine Paketverarbeitung. Fokusänderungen werden im regulären Mess- und Peerzyklus übernommen. Für dieses Verhalten alle PCs auf 1.0.3 oder neuer aktualisieren.

DYNEX berücksichtigt große Netzwerkflüsse erst nach mindestens 250 ms Laufzeit und 256 KiB übertragenen Daten. Damit werden kurze Interaktionen nicht vorschnell als Downloads gewertet. Große Downloads auf dem eigenen Gaming-PC werden ebenfalls aus seinem Anteil begrenzt. Spiele sowie Discord, Microsoft Teams, Zoom, TeamSpeak und Mumble sind ausgenommen; ebenso reine TCP-ACKs, DNS und privater LAN-Verkehr. Unbekannte Flows sowie veraltete oder nicht sicher zuordenbare Prozess-IDs werden ebenfalls ausgenommen.

Die adaptive Pingreserve ist immer aktiv und hat keinen sichtbaren Ein-/Aus-Schalter. Eine gültige Leerlauf-Baseline des Gaming-Peers kann das Hintergrundbudget am Download-PC anpassen. Das verspricht keine feste Pingzahl oder bestimmte Verbesserung. Eine Prozesserkennung bestätigt keinen Multiplayerstatus oder aktives Match.

Der Filter ist adapter- und systemweit. Andere VPN-, Sicherheits-, Virtualisierungs- oder Filtersoftware kann mit NDISRD um denselben Adapter konkurrieren. DYNEX darf einen belegten oder nicht eindeutig verfügbaren Filter nicht übernehmen; bei Konflikt bleibt die Begrenzung aus. Die Deinstallation unterscheidet deshalb eigene, gemeinsam genutzte und bereits vorher vorhandene Filter.

## Aktualisieren und entfernen

**Rückkehr bei fehlgeschlagenem Update (ab 1.0.6):** Vor dem Überschreiben sichert das Setup `DYNEX.exe` und den Helper nach `%LOCALAPPDATA%\Programs\DYNEX\rollback`. Danach startet es die neue Version einmal mit `--self-test <Version>`; sie prüft Version, vollständigen Helper und das Lesen der vorhandenen Einstellungen, ohne Netzwerk oder Filter zu starten. Schlägt dieser Test, das Kopieren oder die Netzwerkeinrichtung fehl, stellt das Setup die gesicherte Version samt angezeigter Versionsnummer wieder her und startet sie. Die fehlgeschlagene Version wird in `%LOCALAPPDATA%\DYNEX\updates\rolled-back.txt` vermerkt und nicht automatisch erneut geladen; DYNEX zeigt beim Start einen Hinweis. Einstellungen, Kopplungen und Identität liegen außerhalb des Programmordners und bleiben unberührt. Nach erfolgreichem Update wird die Sicherung gelöscht. Kann die Sicherung nicht angelegt werden, bricht das Setup ab, ohne Dateien zu überschreiben (Code 14).

Vor Installation, Aktualisierung oder Deinstallation sendet das Setup `DYNEX.exe --shutdown` über die benutzerspezifische Shutdown-Steuerung. DYNEX meldet Erfolg erst nach geordnetem Beenden und Freigabe des Singletons. Das Setup wartet bis zu 20 Sekunden auf den bestätigten Abschluss und bricht bei Fehler oder Timeout ab. Es beendet keine fremden Prozesse zwangsweise.

Die Deinstallation gibt zunächst die laufende Paketverarbeitung frei und bereinigt dann die Systemkomponenten. Das Setup speichert ab 1.0.4 einen administrativ geschützten Herkunftsnachweis unter `HKLM\SOFTWARE\DYNEX\Setup`, der Updates überdauert. Einen von DYNEX neu installierten Filter entfernt die letzte registrierte DYNEX-Installation über den offiziellen Windows-Installer. Ein nachweislich schon vorher vorhandener Filter bleibt erhalten. Auch ein noch von einer weiteren registrierten DYNEX-Installation benötigter Filter bleibt bestehen; das Ergebnis nennt diesen Grund.

**Ältere Installationen:** Bis einschließlich 1.0.3 wurde die Herkunft nicht aufgezeichnet. Bei dem bekannten Windows-Packet-Filter-Paket ohne eindeutigen Nachweis fragt die Deinstallation einmal ausdrücklich nach der Entfernung. Bei Nein oder im unbeaufsichtigten Modus wird die Bereinigung angehalten. Das vermeidet eine unbemerkte Entfernung gemeinsam genutzter Komponenten. Andere unbekannte Filterpakete werden nicht per Name oder Registry-Löschung entfernt.

**Bereits deinstalliert:** Starte `DYNEX-Cleanup-1.0.6.exe` auf dem betroffenen PC unter dem Windows-Benutzer, der DYNEX installiert hatte. Das Programm bereinigt zurückgebliebene Komponenten für den festen damaligen Installationspfad. Es installiert keine Voraussetzungen und startet keine Messung oder Paketverarbeitung. Ist die DYNEX-App noch installiert, fordert es die normale Deinstallation an. Die gleiche Herkunftsprüfung und gegebenenfalls ausdrückliche Bestätigung gelten auch hier. Alternativ kann das bekannte Paket über **Windows-Einstellungen → Apps → Installierte Apps → Windows Packet Filter x64** mit dessen offiziellem Uninstaller entfernt werden; dies entfernt allein noch keine übrigen DYNEX-Daten.

Die Bereinigung entfernt die eigene unveränderte Firewallregel, eigene Autostart- und Verknüpfungseinträge sowie das lokale `%LOCALAPPDATA%\DYNEX`-Profil einschließlich Einstellungen, Kopplungsschlüsseln, Setup-Cache und heruntergeladenen Updates. Fremde gleichnamige Einträge werden erhalten. Zusätzliche Dateien im Programmordner werden nicht pauschal gelöscht. Verzeichnisverknüpfungen werden nicht in andere Ordner verfolgt.

**Fehler und Neustart:** Wird UAC abgebrochen oder kann eine eigene Systemänderung nicht entfernt werden, meldet die normale Deinstallation einen Fehler und lässt App und Uninstaller für einen erneuten Versuch bestehen. Details stehen in `uninstall-result.txt` beziehungsweise `user-cleanup-result.txt` im Programmordner. Die Filterentfernung kann die Verbindung kurz unterbrechen. Windows-Installer-Code 3010 bedeutet, dass Windows noch neu gestartet werden muss; DYNEX startet den PC nicht automatisch neu. Ein Fehler mitten in der Bereinigung kann bedeuten, dass einzelne Schritte bereits erfolgreich waren; erneute Ausführung ist vorgesehen.

DYNEX setzt keine kompletten Netzwerkadapter oder Netzwerkprofile zurück und ändert weder Router-, MTU-, DNS- noch globale QoS-Einstellungen. Andere Programme, die einen früher von DYNEX installierten oder ausdrücklich zur Entfernung bestätigten Filter nachträglich mitverwenden, lassen sich nicht vollständig automatisch ermitteln. Vor der bestätigten Entfernung sollte dieser gemeinsame Bedarf geprüft werden.

## Lizenz und Grenzen

Der Paketfilterhersteller nennt private, akademische und Evaluierungsnutzung als kostenlos. Das ist keine pauschale Erlaubnis für kommerzielle Nutzung oder Redistribution; dafür ist eine gesonderte Herstellerlizenz zu prüfen. Das MSI wird aus diesem Grund nicht in den DYNEX-Installer aufgenommen.

Siehe [Shaping und Grenzen](shaping.md) sowie [Backend-Details](ndis-backend.md), falls diese Datei im Paket vorliegt.
