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
öffnet DYNEX sich wieder, bei Windows-Autostart im Infobereich. Vorhandene
Netzwerkvoraussetzungen mit vorhandenem Besitznachweis werden übersprungen. Fehlt die private Heimnetz-Firewallfreigabe,
der Netzwerkfilter oder der für die spätere Deinstallation benötigte Nachweis, richtet das Setup dies nach Windows-UAC-Bestätigung ein.
Bei Upgrades alter Versionen bleibt eine unbekannte Filterherkunft ausdrücklich unbekannt; vorhandene Filter werden dabei nicht neu installiert.
Die gespeicherten Einstellungen bleiben erhalten.

Die Funktion ist für die Installation unter `%LOCALAPPDATA%\Programs\DYNEX`
verfügbar. Entwicklungsstarts, kopierte EXEs und isolierte Prüfungen starten keinen
Updater. Der Cache liegt unter `%LOCALAPPDATA%\DYNEX\updates`.

## Prüfung und Grenzen

Die App akzeptiert ausschließlich stabile Versionen mit dem Tag `vMAJOR.MINOR.PATCH`
und dem passenden `DYNEX-Setup-MAJOR.MINOR.PATCH.exe` aus dem festgelegten
GitHub-Repository. Sie prüft HTTPS-Ziel, Dateigröße und SHA-256-Digest; auch vor der
Installation wird die gespeicherte Datei erneut geprüft. Unvollständige Downloads
werden nicht installiert. Die Anwendung enthält keine GitHub-Zugangsdaten.

Ab 1.0.6 prüft DYNEX zusätzlich die Authenticode-Signatur des Setups mit Windows
(`WinVerifyTrust`), direkt nach dem Download und noch einmal unmittelbar vor dem Start:

- Ist die laufende DYNEX.exe gültig signiert, muss das Update gültig und vom selben
  Herausgeber (gleicher Zertifikatsantragsteller, also auch nach Zertifikatserneuerung)
  signiert sein. Unsignierte Updates oder ein anderer Herausgeber werden verworfen.
- Eine vorhandene, aber ungültige Signatur wird immer verworfen.
- Unsignierte Altinstallationen (bis 1.0.5) akzeptieren weiterhin hash-geprüfte Updates.
  Das erste signierte Update stellt danach dauerhaft auf Herausgeberprüfung um.

Verworfene Dateien werden gelöscht; die Anzeige lautet „Update verworfen: …“.
Eine Sperrlistenprüfung über das Netz erfolgt dabei nicht, damit Updates offline
nicht blockieren.

Schlägt die Installation fehl, stellt das Setup die vorherige Version wieder her
([Ablauf](installation.md#aktualisieren-und-entfernen)). Diese Version wird nicht
automatisch erneut geladen; eine spätere höhere Version schon.

Ein separater Installations-Mutex verhindert App-Starts während des Kopierens. Der
Updater wartet auf das tatsächliche Ende seines Startprozesses und bricht ab, wenn
eine andere DYNEX-Instanz läuft; er beendet diese nicht. Ein verbrauchter
Installationsauftrag wird nicht beim nächsten Start erneut ausgeführt. Bei Problemen
bleibt eine manuelle Neuinstallation über das GitHub-Release möglich. Updates lassen
sich jederzeit durch Entfernen des Hakens ausschalten.

## Signieren

`scripts/build.ps1` signiert `DYNEX.exe`, `Dynex.Shaper.exe`/`.dll`, das Setup, den
darin erzeugten Uninstaller und das Bereinigungsprogramm (SHA-256, RFC-3161-Zeitstempel)
und prüft jede Signatur sofort mit `signtool verify /pa`. Benötigt werden das Windows SDK
(`signtool.exe`) und ein Code-Signing-Zertifikat:

```powershell
# Zertifikat im Windows-Zertifikatspeicher oder auf einem Token (empfohlen, z. B. EV/OV):
./scripts/build.ps1 -Version 1.0.6 -SignCertificateThumbprint <SHA1-Fingerabdruck> -RequireSignature
# oder PFX-Datei; das Kennwort nur über die Umgebung, nie als Parameter:
$env:DYNEX_SIGN_PFX_PASSWORD = '…'
./scripts/build.ps1 -Version 1.0.6 -SignPfxPath C:\keys\dynex.pfx -RequireSignature
```

`-RequireSignature` bricht ab, wenn kein Zertifikat konfiguriert ist oder eine Datei nicht
gültig signiert wurde; für Releases immer verwenden. Ohne Zertifikat entsteht ein
unsignierter Entwicklungsbuild mit Warnung. Der Build schreibt zu Setup und
Bereinigungsprogramm jeweils eine `.sha256`-Datei und gibt Hash und Signaturstatus aus.

Grenzen: Eine Signatur macht die Herkunft überprüfbar und verhindert Manipulation nach
dem Signieren. Windows SmartScreen kann bei neuen Zertifikaten trotzdem warnen, bis
genügend Reputation besteht; EV-Zertifikate bzw. Microsoft Trusted Signing verkürzen das.
Der Packet-Filter-Treiber stammt weiterhin vom Hersteller und wird separat geprüft.

## Folgeversion veröffentlichen

Die Versionsnummer der Desktop-App und des Installers muss übereinstimmen.
Nach Tests und `scripts/build.ps1 -Version X.Y.Z -RequireSignature …` das geprüfte, signierte
Setup sowie die erzeugte Datei `DYNEX-Setup-X.Y.Z.exe.sha256` als Assets eines stabilen GitHub-Releases
`vX.Y.Z` veröffentlichen. Die Hashdatei enthält den SHA-256-Hash und den exakten
Dateinamen. Das Release als neuestes Release markieren. Nur Commits oder Tags ohne
veröffentlichtes Release lösen kein Update aus.

GitHub beschreibt den verwendeten Endpunkt und die SHA-256-Digests in der
[Releases-API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release).
