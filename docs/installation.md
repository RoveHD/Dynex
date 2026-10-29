# DYNEX 1.0.6 installieren und entfernen

> **Aktueller Betrieb: reine Priorisierung.** Budget-, Limit- und automatische Speedtest-Beschreibungen in diesem historischen Dokument gelten nur für ältere Versionen. Es werden keine Download-/Uploadlimits mehr ausgeführt; alte Einstellungen werden neutralisiert. Der manuelle Speedtest bleibt Diagnose. Aktuelle Abläufe: [README](../README.md), [Paketpriorisierung](qos.md), [Architektur](architecture.md).

Das Release-Setup `artifacts/DYNEX-Setup-1.0.6.exe` installiert DYNEX pro Windows-Benutzer unter `%LOCALAPPDATA%\Programs\DYNEX`. Es enthält die Desktop-App und den erforderlichen `Dynex.Shaper.exe`-Helper. Die App bleibt pro Benutzer installiert und benötigt keine separate .NET-Laufzeit. Für Netzwerkvoraussetzungen und deren Besitznachweis fordert das Setup bei Bedarf Administratorrechte an. Ab 1.0.6 kann der Build App, Helper, Setup, Uninstaller und Bereinigungsprogramm mit einem Authenticode-Zertifikat signieren ([Signieren](updates.md#signieren)); ob ein Release signiert ist, zeigt **Eigenschaften → Digitale Signaturen** der Setup-Datei. `artifacts/DYNEX-Cleanup-1.0.6.exe` bereinigt bereits deinstallierte ältere Versionen, ohne DYNEX neu zu installieren.

## Start im privaten LAN

Starte DYNEX auf den PCs im selben privaten Windows-Netzwerk. AutoLAN, Windows-Autostart und automatischer Schutz beim Spielen sind standardmäßig eingeschaltet; für den ersten Kontakt ist kein Gruppencode nötig. Eine neue Peer-Identität im privaten LAN wird beim ersten Kontakt automatisch vertraut. Optional kann weiterhin eine private gemeinsame Gruppe genutzt werden.

Der Speedtest startet automatisch genau einmal. Auch nach Fehler oder Abbruch gibt es beim nächsten Programmstart keinen automatischen Wiederholungsversuch. Danach lässt er sich über den Speedtest-Knopf manuell starten. Automatische GitHub-Updates lassen sich separat in den Einstellungen anhaken; [Details zum Ablauf](updates.md).

Der lokale Windows-PC-Name wird automatisch verwendet. Die Anschlusskapazität wird einmal gemessen und gespeichert. Ab 0.3.1 stimmen die PCs ihre unterschiedlichen Messwerte automatisch ab und teilen den kleinsten daraus errechneten Downloadtopf. Die Reserve je Gaming-PC beträgt automatisch `min(10 Mbit/s, 5 % der Kapazität)`; das Standard-Hintergrundbudget beträgt 65 %. Installiere die aktuelle Version auf jedem beteiligten PC und schalte jeweils die Downloadbegrenzung ein. Ein bereits eingerichteter Netzwerkfilter muss beim App-Update nicht erneut installiert werden.

## Netzwerkeinrichtung im Setup

Die aktuelle Windows-Version richtet einmalig den Dienst **DYNEX Paketpriorisierung** (`DYNEX.Prioritization`) ein. Der Dienst und seine geprüften Laufzeitdateien liegen unter `%ProgramFiles%\DYNEX\Prioritization\<Paket-Hash>` mit Schreibrechten nur für Administratoren und SYSTEM. Die normale Desktop-App bleibt im Benutzerordner und läuft ohne Administratorrechte. Start, Autostart, Netzwechsel und ein Neustart des Netzwerkhelfers lösen keine UAC-Abfrage mehr aus. Fehlt der Dienst, zeigt DYNEX die einmalige Einrichtung an; es startet keine wiederholten Administratorabfragen.

Die Windows-Rückfrage bleibt für die erstmalige Einrichtung, Änderungen am geschützten Dienstpaket bei einem Update und die Systembereinigung erhalten. DYNEX schaltet UAC nicht ab. Der Dienst akzeptiert ausschließlich lokal authentifizierte Anforderungen der registrierten DYNEX-Benutzerinstallation und startet nur seinen eigenen Netzwerkhelfer. Beendet sich die Desktop-App, läuft der Helfer in einen Timeout oder wird der Dienst beendet, gibt der Helfer die Paketverarbeitung frei. Die letzte registrierte Installation entfernt bei der Deinstallation auch den Dienst und seine nachgewiesenen Dateien.

Das DYNEX-Setup prüft den Netzwerkfilter und eine eigene Firewallfreigabe. Fehlt der Filter, lädt es das offizielle Windows Packet Filter x64 MSI direkt vom Hersteller in den Cache des installierenden Benutzers. Dateigröße, fester SHA-256 und Authenticode-Signatur werden vor Ausführung geprüft; die erhöhte Phase prüft erneut und hält die geprüfte Datei bis zum Installationsende gesperrt. Das MSI und der Kernel-Treiber werden nicht im DYNEX-Paket weiterverteilt.

Eine Windows-UAC-Bestätigung erlaubt die nötigen Systemänderungen. Der offizielle MSI-Installer läuft mit Fortschrittsanzeige ohne zusätzlichen Einrichtungsassistenten. Die Verbindung kann kurz unterbrochen werden. Bereits registrierte Filter werden übersprungen und nicht neu installiert. Ein nötiger Neustart wird als Ergebnis 3010 angezeigt; DYNEX erzwingt ihn nicht. Bei Abbruch, fehlendem Internet oder einem Fehler meldet das Setup eine unvollständige Einrichtung und einen Fehlercode. Details stehen unter `%LOCALAPPDATA%\Programs\DYNEX\setup-result.txt`; Setup erneut starten oder die Einrichtung in DYNEX verwenden.

Die eigene Firewallregel gilt nur für die installierte DYNEX.exe, eingehendes UDP 39274, das Windows-Profil Privat und Absender im lokalen Subnetz. Öffentliche und Domänennetze werden dadurch nicht freigegeben. DYNEX ändert das Netzwerkprofil nicht automatisch. Ein öffentliches Windows-Netzwerk wird von der automatischen privaten Gerätesuche nicht freigegeben; technische Befunde stehen im Diagnoseexport. Auf allen beteiligten PCs muss DYNEX laufen. Routerseitige Client-Isolation oder zusätzliche Firewallregeln können die Verbindung weiterhin verhindern.

Der Speedtest unterscheidet jetzt zwischen laufender Prüfung, nicht verfügbarer PC-Verbindung und einem tatsächlich offenen Spiel. Die Messung dient nur der Diagnose und steuert keine Downloadlimits. Ein Netzwerkfehler wird nicht als Spielpause bezeichnet.

## Geräteanzeige

Die Paketpriorisierung arbeitet lokal. Andere DYNEX-Geräte ergänzen die Geräteübersicht um ihre bestätigten Status- und Messwerte.

DYNEX sucht standardmäßig im selben privaten Netzwerk. **Verbundene Geräte → Gerätesuche aktualisieren** fordert eine neue Suche an. Auf den anderen Geräten muss DYNEX laufen; das Setup richtet die private Firewallfreigabe ein. Die App verändert keine Windows-Netzwerkprofile und umgeht keine Router-Geräteisolation. Fehlende Antworten beweisen keine bestimmte Router- oder Firewallursache. Technische Befunde stehen im Diagnoseexport unter **Einstellungen → Allgemein → Diagnose**.

Bestehende private Gruppen, direkte Gegenadressen und Netzwerkprofile bleiben erhalten. Die frühere manuelle Gruppen-/Adressoberfläche entfällt. Bei einer bestehenden privaten Gruppe ist der Wechsel mit **Automatische Gerätesuche aktivieren** weiterhin ausdrücklich möglich; der gespeicherte Gruppenschlüssel wird dabei nicht gelöscht.

Nach Standby oder Netzwechsel baut DYNEX eine frische authentifizierte Sitzung auf. Netzwerkprofile übernehmen weiterhin die zum Anschluss gespeicherten Mess- und Verbindungseinstellungen. Normales Speichern allgemeiner Einstellungen überschreibt diese Werte nicht.

## Wirkungsbereich

Mit **Paketpriorisierung aktivieren (Spiele, Sprache, Video)** bleibt die Priorisierung auch ohne laufendes Spiel aktiv. Erkannte Spiele und Sprache erhalten Vorrang vor nachgewiesenen Videoverbindungen. Abschalten, Diagnose-Speedtest und Beenden geben die Capture-Sitzung frei. Nach dem Speedtest wird eine weiterhin eingeschaltete Priorisierung wieder aufgenommen.

DYNEX leitet alle Pakete unmittelbar weiter und begrenzt keine Downloads oder Uploads. Eine frische Prozesszuordnung und passende Paket-/Mediennachweise bestimmen die DSCP-Markierung. Browser- und ELFIX-Verkehr wird nicht pauschal als Video behandelt; Details und Grenzen stehen unter [QoS](qos.md).

Der Router muss die Markierungen für Vorrang auswerten. DYNEX konfiguriert weder Router-SQM noch Autorate und garantiert keinen bestimmten Ping. Eine Prozesserkennung bestätigt keinen Multiplayerstatus oder ein aktives Match.

Der Filter ist adapter- und systemweit. Andere VPN-, Sicherheits-, Virtualisierungs- oder Filtersoftware kann mit NDISRD um denselben Adapter konkurrieren. DYNEX darf einen belegten oder nicht eindeutig verfügbaren Filter nicht übernehmen; bei Konflikt bleibt die Begrenzung aus. Die Deinstallation unterscheidet deshalb eigene, gemeinsam genutzte und bereits vorher vorhandene Filter.

## Aktualisieren und entfernen

Ab 1.0.24 verschiebt das Setup den gesamten bisherigen Helferordner nach `rollback\shaper`, bevor es die neuen Dateien kopiert. So bleiben keine veralteten Runtime-Dateien im neuen, streng geprüften Paket zurück. Bei einem Fehler wird der vollständige alte Ordner einschließlich Unterordnern zurückverschoben. Solange eine unvollständige frühere Rücksicherung vorhanden ist, bricht ein neues Setup vor dem Überschreiben ab und erhält die Sicherung.

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
