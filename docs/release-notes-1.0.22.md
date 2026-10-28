# DYNEX 1.0.22

Windows startet den Netzwerkhelfer über einen einmalig eingerichteten Dienst. Die wiederkehrende UAC-Abfrage für `Dynex.Shaper.exe` bei normalen App- und Helferstarts entfällt.

- **Einmal einrichten:** Das Setup beziehungsweise die Einrichtung in den Einstellungen aktualisiert den geschützten Dienst. Windows verlangt dafür weiterhin eine Bestätigung; auch spätere Änderungen am Dienstpaket und die Deinstallation können eine Bestätigung benötigen. UAC bleibt eingeschaltet.
- **Geschützter Helfer:** Der Dienst startet ausschließlich seine eigenen geprüften Dateien unter `%ProgramFiles%\DYNEX\Prioritization`. Lokale Verbindung, Windows-Benutzer, Prozess-ID, Startzeit und registrierter Anwendungspfad werden geprüft. Der Desktop läuft weiter ohne Administratorrechte.
- **Sauber beenden:** Desktop-Ende, Netzwechsel, Heartbeat-Ausfall und Dienstende geben die Paketverarbeitung frei. Fehler bei der Freigabe werden nicht als Erfolg ausgegeben. Die letzte registrierte Installation entfernt auch den Dienst.
- **Einrichtung und Updates:** Ein vorhandener Netzwerkfilter allein reicht nicht mehr für die Anzeige einer vollständig eingerichteten Priorisierung. Das Setup prüft alle Helferdateien anhand des eingebetteten Nachweises und lehnt fehlende oder veränderte Dateien ab.

Spiel-, Sprach-, Video- und ELFIX-Erkennung, Übersichten und Messwerte bleiben erhalten. Es gibt weiterhin keine Download- oder Uploadlimits. Die Android-App erhält die passende Versionsnummer und verwendet ihren bestehenden Update-Schlüssel; der UAC-Fix betrifft Windows.

Die isolierten Prüfungen für Paketverarbeitung, Benutzer-/Pipe-Authentifizierung, Runtime, Einrichtung und Installationspaket bleiben Voraussetzung des Releases. Ein Test ohne Paketaufnahme bestätigt keine Router-Priorisierung oder Verbesserung des Pings unter echter Netzlast.

Windows-Setup und Programme bleiben ohne eingerichtetes Code-Signing-Zertifikat unsigniert; SHA-256-Prüfsummen liegen bei. Die Umstellung auf den Dienst erfordert einmalig die Windows-Bestätigung im neuen Setup.
