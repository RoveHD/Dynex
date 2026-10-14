# Updates von GitHub

Unter **Einstellungen → Updates** gibt es **Updates automatisch im Hintergrund herunterladen**.
Der Haken ist anfangs aus. Änderungen werden sofort gespeichert; ein zusätzlicher
Klick auf „Einstellungen speichern“ ist dafür nicht nötig. Die laufende
Downloadbegrenzung wird beim Umschalten nicht neu gestartet.

Mit Haken prüft die installierte App beim Start und anschließend alle sechs Stunden
das neueste stabile GitHub-Release. **Jetzt prüfen** startet die Prüfung sofort.
Ohne Haken zeigt diese manuelle Prüfung lediglich an, ob eine Version verfügbar ist.
Es wird dann nichts heruntergeladen oder installiert. Ausschalten bricht einen
laufenden Update-Download ab und verhindert auch die Installation vorgemerkter Updates.

Update-Downloads warten, solange DYNEX auf diesem oder einem verbundenen PC ein geöffnetes Spiel
erkennt (auch im Hintergrund) oder die Internetgeschwindigkeit misst. Beginnt während des Downloads ein
Spiel, wird der Download abgebrochen und nach dem Spielen neu versucht. Außerhalb
dieser Zeiten liest DYNEX höchstens 512 KiB pro Sekunde. Die Anzeige meldet Prüfung,
Warten, Download, Fehler oder die bereitliegende Version.

Ein vollständig geladenes Update wird **beim nächsten regulären Programmstart**
installiert, bevor Messung und Netzwerkhelper starten. Die App schließt sich nicht
während einer laufenden Sitzung. Fenster schließen minimiert DYNEX weiterhin in
den Infobereich; zum vollständigen Beenden dient dessen Menü. Nach der Installation
öffnet DYNEX sich wieder, bei Windows-Autostart im Infobereich. Ein Update installiert
keinen neuen Netzwerkfilter und ändert die gespeicherten Einstellungen nicht.

Die Funktion ist für die Installation unter `%LOCALAPPDATA%\Programs\DYNEX`
verfügbar. Entwicklungsstarts, kopierte EXEs und isolierte Prüfungen starten keinen
Updater. Der Cache liegt unter `%LOCALAPPDATA%\DYNEX\updates`.

## Prüfung und Grenzen

Die App akzeptiert ausschließlich stabile Versionen mit dem Tag `vMAJOR.MINOR.PATCH`
und dem passenden `DYNEX-Setup-MAJOR.MINOR.PATCH.exe` aus dem festgelegten
GitHub-Repository. Sie prüft HTTPS-Ziel, Dateigröße und SHA-256-Digest; auch vor der
Installation wird die gespeicherte Datei erneut geprüft. Unvollständige Downloads
werden nicht installiert. Die Anwendung enthält keine GitHub-Zugangsdaten.

Ein separater Installations-Mutex verhindert App-Starts während des Kopierens. Der
Updater wartet auf das tatsächliche Ende seines Startprozesses und bricht ab, wenn
eine andere DYNEX-Instanz läuft; er beendet diese nicht. Ein verbrauchter
Installationsauftrag wird nicht beim nächsten Start erneut ausgeführt. Bei Problemen
bleibt eine manuelle Neuinstallation über das GitHub-Release möglich. Updates lassen
sich jederzeit durch Entfernen des Hakens ausschalten.

DYNEX-Installer sind derzeit nicht code-signiert. Die Prüfsumme erkennt beschädigte
Dateien und bindet den Download an die über GitHub gelieferte Freigabe; sie ersetzt
keine unabhängige Herausgebersignatur. Windows kann beim manuellen Start eine
SmartScreen-Rückfrage anzeigen.

## Folgeversion veröffentlichen

Die Versionsnummer der Desktop-App und des Installers muss übereinstimmen.
Nach Tests und `scripts/build.ps1 -Version X.Y.Z` das geprüfte Setup sowie eine
Datei `DYNEX-Setup-X.Y.Z.exe.sha256` als Assets eines stabilen GitHub-Releases
`vX.Y.Z` veröffentlichen. Die Hashdatei enthält den SHA-256-Hash und den exakten
Dateinamen. Das Release als neuestes Release markieren. Nur Commits oder Tags ohne
veröffentlichtes Release lösen kein Update aus.

GitHub beschreibt den verwendeten Endpunkt und die SHA-256-Digests in der
[Releases-API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release).
