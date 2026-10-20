# Automatische ELFIX-Medienmeldung unter Windows

Die passive Erkennung allein kann einen beliebigen verschlüsselten ELFIX-Hoster nicht zuverlässig als Video einordnen. Deshalb kann ELFIX die Medienart direkt aus seiner Chromium-Netzsitzung melden. Die Integration muss in beiden Apps enthalten sein; vorhandene veröffentlichte Versionen erhalten die Funktion nicht durch eine bloße Einstellung.

## Verhalten

ELFIX beobachtet die Antworten in der Hoster- und Player-Netzsitzung. Erfolgreiche `video/*`-, HLS- und DASH-Antworten sowie von Chromium ausdrücklich als Medien erkannte Antworten mit generischem MIME liefern den Hinweis. Cache-Antworten, Fehler, widersprüchliche Content-Types, Dateianhänge und bekannte Downloads werden nicht als neue Medienverbindungen gemeldet. Die tatsächliche Server-IP stammt aus der Chromium-Antwort; sie wird nicht über DNS geraten. Die Programmdatei allein ist kein Mediennachweis.

Die Windows-Apps verbinden sich ohne Assistenten oder manuelle Kopplung. Es gibt keinen zusätzlichen Netzwerk-Port. Wenn DYNEX nicht läuft, bleibt die Wiedergabe unverändert; Wiederverbindungen sind begrenzt. Nur solange Medienhinweise bestehen, werden sie alle fünf Sekunden erneuert. Fertige Übertragungen verlieren ihren Hinweis nach 15 Sekunden; eine abgebrochene Pipe widerruft den DYNEX-Hinweis sofort.

DYNEX markiert passende ausgehende Pakete mit AF41. Spiele und Sprache behalten ihren Vorrang. Alle Pakete bleiben unmittelbar und unbegrenzt weitergeleitet. Der Router muss die Prioritäten separat auswerten.

## Bindung und Datenschutz

Der Pipe-Name enthält einen SHA-256-abgeleiteten Bezeichner des lokalen Benutzerverzeichnisses. Die Windows-Pipe ist auf den tatsächlichen Benutzer beschränkt, auch wenn der Helfer erhöht läuft. DYNEX bestimmt die Client-PID selbst und prüft ELFIX-Name, echten EXE-Pfad, Benutzer und Startzeit. Gemeldete Prozess-IDs müssen zu derselben ELFIX-Programmdatei gehören. Der Paketpfad benötigt zusätzlich eine frische Betriebssystem-Zuordnung des betreffenden TCP-/UDP-Sockets.

Eine Nachricht enthält nur Protokollversion, ELFIX-Prozess-IDs sowie IP-Adresse und Port der Medienserver. DYNEX erhält keine Video-URLs, Titel, Cookies oder Medieninhalte. Es findet weder eine TLS-Entschlüsselung noch ein Zugriff auf Prozessspeicher statt. Die Prozessabfrage verwendet ausschließlich Query-Limited-Rechte und nur für zuvor als ELFIX identifizierte Prozesse.

Nachrichten sind auf 16 KiB, 32 Prozess-IDs und 64 Server-Endpunkte begrenzt. Ungültige Daten, Überflutung und Verbindungsabbruch löschen die Freigabe. Ein Empfangsfehler dieser optionalen Integration stoppt die Spielpriorisierung nicht.

## Grenzen

Die Anwendungsinformation gilt innerhalb der bestätigten ELFIX-Prozesse für den tatsächlich gemeldeten Server-IP-/Port-Endpunkt. Wenn ELFIX mehrere verschlüsselte Anfragen an denselben Endpunkt stellt, kann DYNEX einzelne HTTP/2-/HTTP/3-Anfragen oder gemeinsam genutzte Transportverbindungen nicht auseinandernehmen. Der Hinweis gilt nicht für andere Anwendungen, IP-Adressen oder Ports. Eine Neuverbindung wird anhand der aktuellen Betriebssystem-Zuordnung geprüft; gespeicherte PIDs ohne passende Startzeit genügen nicht.

Ohne aktuelle Anwendungsmeldung bleiben nur die bisherigen passiven SNI-/HTTP-Nachweise. Bei einem bereits laufenden Stream kann ein neu gestartetes ELFIX keine rückwirkende Netzantwort liefern; die nächste echte Medienanfrage liefert den Hinweis.

Die API wurde anhand von [Electron WebRequest](https://www.electronjs.org/docs/latest/api/web-request) und dem [nativen Electron-44.4.3-Code für tatsächliche Antwort-IP und Header](https://github.com/electron/electron/blob/v44.4.3/shell/browser/api/electron_api_web_request.cc) geprüft.

## Prüfungen

`dotnet run --project tests/Dynex.Shaper.Tests -c Release` prüft Parser, Prozess-/Endpunktbindung, Prioritäten, Ablauf und Pipe-Lebenszyklus. `DYNEX_TEST_NODE_MEDIA=1` ergänzt einen echten Node-Pipe-Client und einen unabhängigen Vergleich des Pipe-Namens, wenn Node installiert ist.

In ELFIX prüfen `node tests/dynexvideotest.js` und `electron tests/dynexvideo-electrontest.js` den Sender und echte lokale Chromium-Anfragen. Die Electron-Prüfung verwendet ein isoliertes Profil, unsichtbare Fenster und lokale Testserver. Die zusätzliche gekoppelte Prüfung verwendet den echten Windows-Prozessprüfer und echte Socket-Zuordnung; die daraus erzeugte AF41-Markierung wird an einem Testpaket geprüft. Sie öffnet keinen Kernel-Filter und ersetzt keinen Belastungstest am Router.
