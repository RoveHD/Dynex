# DYNEX 1.0.21

Dieser Windows-Fix korrigiert die Discord-Videoerkennung aus 1.0.20. Kleine Kamera- und Bildschirmframes konnten bisher grundsätzlich unklassifiziert bleiben, wenn jedes Bild in nur einem RTP-Paket übertragen wurde.

- **Kleine Videoframes:** Mehrere vollständige Ein-Paket-Frames können jetzt über ihren passenden 90-kHz-Zeitverlauf als Video erkannt werden. Der bisherige Nachweis über fragmentierte Frames bleibt erhalten.
- **Paketverlust und große Keyframes:** Verlorene Frame-Endmarker oder normale Sequenzlücken löschen nicht mehr ständig die gesamte Videoevidenz.
- **Vertauschte und doppelte Pakete:** Eine bereits bestätigte Videoquelle behält ihre AF41-Klasse bei üblichen Vertauschungen. Solche Pakete verlängern keine abgelaufene Freigabe.
- **Sprache bleibt getrennt:** Discord-Opus bleibt Sprache; ein Programmname, eine Paketgröße oder ein dynamischer Payload-Type allein macht weiterhin keinen Verkehr zum Video. Andere Programme und unbekannte Übertragungen übernehmen keine Discord-Freigabe.

Spiel- und Sprachpriorisierung, die bestehende Streaming-/ELFIX-Erkennung sowie Übersichten und Messwerte bleiben erhalten. Es gibt weiterhin keine Download-/Uploadlimits. Die Android-App bleibt eine Begleit-App; der Discord-Fix läuft auf Windows. Für diese Änderung ist kein neuer ELFIX-Release erforderlich.

Lokal bestanden **889 Shaper-Prüfungen**, **10 gezielte Runtime-Prüfungen**, Desktop-Build und Anti-Cheat-API-Guard. Zusätzlich wurde die tatsächliche Windows-Socketzuordnung eines laufenden Discord-PTB-Prozesses mit künstlichen RTP-Paketen in beiden Richtungen geprüft. Ein echter Videoanruf, ein physischer Routerlasttest und eine Installation dieses Releases auf Nutzergeräten wurden damit nicht bestätigt. Details: [Videoerkennung und Grenzen](video-detection.md).

Windows-Setup und Programme bleiben mangels eingerichtetem Code-Signing-Zertifikat unsigniert; SHA-256-Prüfsummen liegen bei. Die Android-APK verwendet den bestehenden Update-Schlüssel.
