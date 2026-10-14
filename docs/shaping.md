# Downloadbegrenzung und Grenzen

DYNEX 1.0.4 verwendet einen separaten, erforderlichen `Dynex.Shaper.exe`-Helper. Er kommuniziert mit dem regulär installierten NDISRD-Netzwerkfilter direkt über Windows `DeviceIoControl`; es wird weder `ndisapi.dll` noch WinDivert verwendet. Die Desktop-App selbst muss nicht erhöht laufen. Downloadbegrenzung und [QoS für Spiele und Sprachchat](qos.md) laufen nur während ein erkanntes Spiel auf diesem oder einem verbundenen PC im Vordergrund ist. Ohne Gaming wird die Capture-Sitzung freigegeben; der installierte Treiber wird dabei nicht deinstalliert. Die vollständige Deinstallation und nachträgliche Bereinigung sind in [Installation](installation.md#aktualisieren-und-entfernen) beschrieben.

## Einmalige Einrichtung und Aktivierung

DYNEX lädt einen fehlenden Netzwerkfilter während des Hauptsetups direkt vom Hersteller. Die App prüft die feste Größe (819.200 Bytes), den SHA-256-Pin `9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c` und Windows-Authenticode-Vertrauen. Nach der Windows-UAC-Bestätigung richtet das Setup ihn ohne zusätzlichen Assistenten und ohne erzwungenen Neustart ein. Die Netzwerkverbindung kann dabei kurz unterbrochen werden. Das MSI wird nicht in DYNEX weitergegeben. Vorhandene Komponenten werden übersprungen; Details stehen unter [Installation](installation.md).

Der Filterinstaller führt Änderungen an Zertifikatsspeicher und Netzwerkadaptern aus. Der erste erforderliche Helperstart während Gaming kann ebenfalls eine Windows-UAC-Abfrage auslösen. Ohne den Helper, ohne erfolgreich eingerichteten Filter oder bei einer Treiber-/Adapterstörung zeigt DYNEX den Fehler und meldet kein aktives Limit.

## Welche Flows begrenzt werden

Eine einzelne Verbindung wird erst nach mindestens 250 ms, 256 KiB und durchschnittlich mindestens 256 KiB/s zum großen Download. Bei vielen parallelen Verbindungen desselben Programms greift zusätzlich eine gemeinsame Erkennung: mindestens 1 MiB Programmdaten in einem höchstens einsekündigen Fenster, wobei jede erfasste Verbindung selbst mindestens 250 ms, 64 KiB und 32 KiB/s erreichen muss. So können parallele Downloads die Erkennung nicht allein durch kleine Einzelverbindungen umgehen. Nach fünf Sekunden ohne Nutzdaten wird eine Verbindung erneut eingeordnet. Diese Schwellen sind lokale Heuristiken, keine perfekte Erkennung der Absicht eines Programms.

DYNEX begrenzt solche Verbindungen nach der gemeinsamen Budgetplanung; erkannte Gaming-Verbindungen auf demselben PC bleiben ausgenommen. Außerdem ausgenommen sind Discord, Microsoft Teams, Zoom, TeamSpeak und Mumble, reine TCP-ACKs, DNS und LAN-Verkehr. Unbekannte Verbindungen und veraltete oder nicht verlässlich einem laufenden Prozess zuordenbare Prozess-IDs werden vorsichtshalber nicht begrenzt. Die Sprachchat-Ausnahme gilt für die genannten Programme insgesamt, also auch deren Downloads. Kleine Verbindungen umgehen die Download-Warteschlange; innerhalb einer bereits als groß erkannten TCP-Verbindung werden Nutzdaten nicht paketweise überholt.

Diese Einordnung ist lokale Flow-/Prozesszuordnung, keine Spielserver- oder Match-Erkennung. Eine erkannte Spiel-EXE kann im Menü stehen. Ein konfigurierter Prozessname beweist keinen Multiplayerstatus.

## Adapter- und Systemgrenzen

NDISRD arbeitet auf Netzwerkadaptern und ist systemweit. Andere VPN-, Sicherheits-, Virtualisierungs- oder Paketfiltersoftware kann dieselben Adapter beanspruchen. DYNEX übernimmt keinen bereits belegten Filter und bricht bei einer nicht eindeutigen oder fehlgeschlagenen Adapteröffnung ab, statt den Verkehr unkontrolliert umzuleiten. Die konkrete Koexistenz mit real installierten Netzwerkfiltern wurde noch nicht praktisch verifiziert.

Ein lokaler Downloadfilter kann die Zustellung an Anwendungen verzögern. Eingehende Daten haben den Router und Internetanschluss aber bereits passiert; daher gibt es keine garantierte Reduktion des WAN-Verbrauchs, keinen festen Durchsatz und keine 30-ms- oder Pingverbesserungsgarantie. Upload-Sättigung, WLAN-Störungen, andere PCs ohne DYNEX, VPNs und Providerprobleme bleiben außerhalb der Kontrolle.

Der Paketfilter wird separat vom DYNEX-Helper eingerichtet. DYNEX entfernt ihn bei Deinstallation nicht, weil andere Apps davon abhängen können. Falls du ihn nicht mehr brauchst, deinstalliere **Windows Packet Filter x64** über **Windows-Einstellungen → Apps → Installierte Apps**, nachdem du Abhängigkeiten anderer Netzwerkprogramme geprüft hast. Ein erforderlicher Windows-Neustart wird von Windows angezeigt; DYNEX erzwingt keinen.

## Herkunft und Lizenz

Das x64-MSI stammt aus dem [offiziellen GitHub-Release v3.6.2](https://github.com/wiresock/ndisapi/releases/tag/v3.6.2) und wird über die Downloadseite des [Herstellers NT KERNEL](https://www.ntkernel.com/windows-packet-filter/download/) verlinkt. Der Release-API-Pin ist `sha256:9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c`. Das MSI wurde separat auf Authenticode geprüft und als `Valid` bestätigt. Der Hersteller nennt private/akademische Nutzung und Evaluation als kostenlos. Für kommerzielle Verwendung oder Weitergabe ist eine gesonderte Lizenzprüfung erforderlich; DYNEX bundelt das MSI nicht.

Die MSI-Inventur, Custom-Action-Übersicht und Prüfdaten der extrahierten Signaturdateien stehen lokal in `artifacts/dependencies/windows-packet-filter/inspection-v3.6.2/INSPECTION.txt` und sind nicht Bestandteil des DYNEX-Setups.
