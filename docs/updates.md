# Updates von GitHub

Unter **Einstellungen → Updates** gibt es **Updates automatisch herunterladen und installieren**.
Der Haken ist anfangs aus. Änderungen werden sofort gespeichert; ein zusätzlicher
Klick auf „Einstellungen speichern“ ist dafür nicht nötig. Die laufende
Downloadbegrenzung wird beim Umschalten nicht neu gestartet.

Mit Haken prüft die installierte App beim Start und anschließend alle sechs Stunden
das neueste stabile GitHub-Release. **Jetzt prüfen** startet die Prüfung sofort.
Ohne Haken zeigt diese manuelle Prüfung lediglich an, ob eine Version verfügbar ist.
Es wird dann nichts heruntergeladen oder installiert. Ausschalten bricht einen
laufenden Update-Download ab und verhindert auch die Installation vorgemerkter Updates.

Update-Downloads laufen auch dann weiter, wenn DYNEX auf diesem oder einem verbundenen PC ein Spiel
erkennt (auch im Hintergrund); die Rate bleibt auf höchstens 512 KiB pro Sekunde begrenzt. Nur ein
aktiver Internet-Speedtest pausiert den Download. Die Anzeige enthält den Fortschritt in MB und
Prozent. Stehen über 30 Sekunden keine Bytes zur Verfügung, meldet DYNEX einen Fehler; **Jetzt prüfen**
startet einen neuen Versuch. Ausschalten der Option bricht den Download ab und verhindert die Übergabe
eines fertigen Updates an den Installer.

Nach vollständigem Download und erfolgreichem Bestehen der geltenden Hash- und Signaturprüfungen installiert DYNEX das Update
automatisch. Vor der Übergabe wartet es, bis Einstellungen gespeichert, aktive Speedtests und UI-Aktionen beendet
sind. Danach beendet DYNEX den Runtime-Filter sauber, schließt sich und startet den Installer still.
Nach erfolgreichem Installerstart wird der Cachemarker verbraucht; die neue Version wird anschließend
gestartet. Die gespeicherten Einstellungen bleiben erhalten. Vorhandene
Netzwerkvoraussetzungen mit vorhandenem Besitznachweis werden übersprungen. Fehlt die private Heimnetz-Firewallfreigabe,
der Netzwerkfilter oder der für die spätere Deinstallation benötigte Nachweis, richtet das Setup dies nach Windows-UAC-Bestätigung ein.
Bei Upgrades alter Versionen bleibt eine unbekannte Filterherkunft ausdrücklich unbekannt; vorhandene Filter werden dabei nicht neu installiert.
Die gespeicherten Einstellungen bleiben erhalten.

Die Funktion ist für die Installation unter `%LOCALAPPDATA%\Programs\DYNEX`
verfügbar. Entwicklungsstarts, kopierte EXEs und isolierte Prüfungen starten keinen
Updater. Der Cache liegt unter `%LOCALAPPDATA%\DYNEX\updates`.

**Korrektur ab 1.0.28:** Der Desktop wartet auf den geschützten SYSTEM-Helfer mit dessen eingeschränkten Beobachtungsrechten. Die bisher verwendete .NET-Wartefunktion verlangte zusätzliche Prozessrechte und konnte vor dem Installerstart mit „Zugriff verweigert“ abbrechen. Die authentifizierte Freigabebestätigung und das tatsächliche Prozessende bleiben erforderlich. Ist eine ältere Installation bereits betroffen, das aktuelle Setup einmal direkt von GitHub starten; danach steht der korrigierte automatische Updatepfad zur Verfügung.

## Prüfung und Grenzen

Die App akzeptiert ausschließlich stabile Versionen mit dem Tag `vMAJOR.MINOR.PATCH`
und dem passenden `DYNEX-Setup-MAJOR.MINOR.PATCH.exe` aus dem festgelegten
GitHub-Repository. Sie prüft HTTPS-Ziel, Dateigröße und SHA-256-Digest; auch vor der
Installation wird die gespeicherte Datei erneut geprüft. Unvollständige Downloads
werden nicht installiert. Die Anwendung enthält keine GitHub-Zugangsdaten.

**Beta-Kanal (ab 1.0.6):** Mit **Vorabversionen (Beta) erhalten** berücksichtigt DYNEX auch GitHub-Releases,
die als „Pre-release“ markiert sind (gleiches Tag-Format `vX.Y.Z`). Es gilt die höchste gültige Version; Entwürfe
werden nie installiert. Alle Prüfungen (Hash, Signatur, Rückkehr bei Fehlern) sind identisch.

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

Ein gemeinsamer Installations-Mutex verhindert parallele manuelle Setups und automatische
Updates sowie App-Starts während des Kopierens. Die Startprüfung schreibt Version,
Startfehler und Prozess-Exitcode nach `%LOCALAPPDATA%\Programs\DYNEX\update-result.txt`.
Der
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
# Zertifikat im Windows-Zertifikatspeicher oder auf einem Token:
./scripts/build.ps1 -Version 1.0.8 -SignCertificateThumbprint <SHA1-Fingerabdruck> -RequireSignature
# oder PFX-Datei; das Kennwort nur über die Umgebung, nie als Parameter:
$env:DYNEX_SIGN_PFX_PASSWORD = '…'
./scripts/build.ps1 -Version 1.0.8 -SignPfxPath C:\keys\dynex.pfx -RequireSignature
```

`-RequireSignature` bricht ab, wenn kein Zertifikat konfiguriert ist oder eine Datei nicht
gültig signiert wurde. Das Release-Workflow verwendet die PFX-Secrets
`DYNEX_SIGN_PFX_BASE64` und `DYNEX_SIGN_PFX_PASSWORD`: Sobald `DYNEX_SIGN_PFX_BASE64` gesetzt ist,
ist Signieren Pflicht und das Kennwort muss ebenfalls konfiguriert sein. Fehlt das Zertifikatsecret,
wird das Release unsigniert gebaut und in Actions sowie Release-Notizen gewarnt.
Lokale Builds ohne Zertifikat sind ebenfalls unsigniert und warnen. Der Build schreibt zu Setup und
Bereinigungsprogramm jeweils eine `.sha256`-Datei und gibt Hash und Signaturstatus aus.

Grenzen: Eine Signatur macht die Herkunft überprüfbar und verhindert Manipulation nach
dem Signieren. Windows SmartScreen kann bei neuen Zertifikaten trotzdem warnen, bis
genügend Reputation besteht; EV-Zertifikate bzw. Microsoft Trusted Signing verkürzen das.
Der Packet-Filter-Treiber stammt weiterhin vom Hersteller und wird separat geprüft.

## Folgeversion veröffentlichen

**Automatisch (empfohlen):** Version in `src/Dynex.Desktop/Dynex.Desktop.csproj` erhöhen, committen und das Tag
`vX.Y.Z` pushen. `.github/workflows/release.yml` führt alle Tests aus und baut Setup und Bereinigung. Sind die
Repository-Secrets `DYNEX_SIGN_PFX_BASE64` (PFX-Datei Base64-kodiert) und `DYNEX_SIGN_PFX_PASSWORD` vorhanden,
ist Signieren Pflicht; fehlt das Zertifikatsecret `DYNEX_SIGN_PFX_BASE64`, entsteht ein unsigniertes Release mit Warnhinweis in Actions und Release-Notizen. Ist das Zertifikatsecret gesetzt, aber das Kennwort fehlt, schlägt der Build fehl.
Ein Tag erstellt einen **Release-Entwurf**. Der manuelle Workflow kann den Release direkt veröffentlichen oder als
Entwurf anlegen. Den Entwurf nach der Zwei-PC-Abnahme veröffentlichen.

**Manuell:**

Die Versionsnummer der Desktop-App und des Installers muss übereinstimmen.
Nach Tests und `scripts/build.ps1 -Version X.Y.Z -RequireSignature …` das geprüfte, signierte
Setup sowie die erzeugte Datei `DYNEX-Setup-X.Y.Z.exe.sha256` als Assets eines stabilen GitHub-Releases
`vX.Y.Z` veröffentlichen. Die Hashdatei enthält den SHA-256-Hash und den exakten
Dateinamen. Das Release als neuestes Release markieren. Nur Commits oder Tags ohne
veröffentlichtes Release lösen kein Update aus.

GitHub beschreibt den verwendeten Endpunkt und die SHA-256-Digests in der
[Releases-API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release).
