# DYNEX 1.0.20

Die Windows-Videoerkennung deckt zusätzliche Streamingdienste und beobachtbare Kamera-/Bildschirmübertragungen nativer Konferenz-Apps ab. Spiele und Sprache behalten Vorrang vor Video; Downloads und Uploads bleiben ohne Bandbreitenlimit.

- **Mehr Streamingdienste:** Bekannte Medienlieferhosts von Twitch Live, Disney+, Prime Video, Vimeo und Apple TV ergänzen YouTube und Netflix. Webseiten, Chat, Vorschaubilder, Player-Skripte und allgemeine Cloud-CDNs begründen keine pauschale Videopriorität.
- **Discord-Kamera und Bildschirmübertragung:** Zusammenhängende RTP-Framefolgen können Video belegen. Mikrofon und Video werden nach ihren tatsächlichen Streams getrennt; Opus bleibt Sprache. Kleine bestätigte Videofragmente erhalten AF41 statt EF.
- **Teams-Kamera und Bildschirmfreigabe:** Die nativen Windows-Clients werden anhand ihrer tatsächlichen Prozesszuordnung und Microsofts dokumentierten lokalen Standard-Medienports erkannt. Zufällig gleich nummerierte Serverports oder andere Programme reichen nicht aus.
- **Stabilere Streaming-Erkennung:** Vertauscht eintreffende TLS-Handshake-Segmente werden begrenzt zusammengesetzt; widersprüchliche Überlappungen bleiben unklassifiziert. Fortlaufend aktive Videoverbindungen verlieren ihre Zuordnung nicht mehr pauschal nach 30 Minuten. Leerlauf, Verbindungsneustart und Prozesswechsel widerrufen weiterhin alte Hinweise.
- **Bestehende Integration bleibt erhalten:** ELFIX 2.0.75 kann unbekannte verschlüsselte Medienhoster weiterhin automatisch lokal melden. Dafür ist kein weiterer ELFIX-Release nötig.

Die Regeln garantieren keine vollständige Erkennung beliebig verschlüsselter Verbindungen. Unter anderem können Twitch-VODs über andere CDNs, ECH ohne sichtbaren Medienhostname, fremde VPN-Tunnel, sehr kleine Discord-Videoframes und abweichende Teams-Portkonfigurationen unbekannt bleiben. Konkrete Regeln und Quellen: [Videoerkennung](video-detection.md).

Die Android-App bleibt eine WLAN-Begleit-App ohne eigene Video-/Spielpaketpriorisierung. Ihre Version wird für diesen Release mitgeführt; die neue Videoklassifizierung läuft auf Windows. Bestehende AdGuard-Weiterleitung bleibt unbegrenzt.

Lokal bestanden 872 Shaper-Prüfungen einschließlich echter Node-Pipe-Verbindung sowie 226 Runtime-Prüfungen, Desktop-Build und Anti-Cheat-API-Guard. Die automatisierten Paketprüfungen ersetzen keine echte Discord-/Teams-Konferenz oder einen Lasttest am physischen Router. Der Router muss die DSCP-Markierungen auswerten.

Windows-Setup und Programme bleiben mangels eingerichtetem Code-Signing-Zertifikat unsigniert; SHA-256-Prüfsummen liegen bei. Die Android-APK verwendet den bestehenden Update-Schlüssel.
