# Spiel-, Sprach- und Videopaketpriorisierung

DYNEX arbeitet ausschließlich mit Paketklassifizierung und direkter Weiterleitung. Es gibt keine Download-/Uploadwarteschlange, keinen Pacer und kein gemeinsames Bandbreitenbudget im aktiven Paketpfad.

## Markierungen

Ein frischer und eindeutiger Prozess-/Sockettreffer ordnet Pakete einem erkannten Spiel oder Sprachprogramm zu. Kleine interaktive Spiel-TCP-Pakete bis 768 Byte Nutzdaten und Spiel-UDP bis 1.400 Byte sind für **CS4 (32)** geeignet. Große TCP-Assetübertragungen erhalten keine pauschale Spielpriorität. Sprach-UDP ist bis 600 Byte geeignet; konservativ erkannte RTP-Audiofolgen können auch ohne bekannten Programmnamen berücksichtigt werden. Die Sprachklasse hat einen begrenzten Markierungsanteil: Überschreitende Pakete werden normal weitergeleitet, nicht gedrosselt.

Nur ausgehende Pakete werden markiert. IPv4 verwendet das DS-Feld und eine korrigierte IP-Headerprüfsumme; IPv6 das Traffic-Class-Feld. ECN, IPv6-Flowlabel, Transportheader und Nutzdaten bleiben erhalten. Eingehende Antworten übernehmen die ausgehende Markierung nicht automatisch. Die Klassen orientieren sich an [RFC 4594](https://www.rfc-editor.org/rfc/rfc4594.html).

## Video hinter Gaming und Sprache

Während einer aktiven Priorisierungssitzung erhalten nachgewiesene Videoverbindungen **AF41 (34)**. CAKE DiffServ4 ordnet AF41 der Video-Klasse zu; CS4 und EF liegen in der höheren Voice-Klasse. Der Router muss AF41 auch für Antworten vor seiner Download-CAKE-Instanz wiederherstellen, falls der Provider keine passenden Markierungen liefert.

Die Erkennung betrachtet den konkreten Prozess mit Startzeit und die exakte TCP-/UDP-Verbindung. Ein echter TLS-ClientHello-Hostname unter `googlevideo.com` oder `nflxvideo.net` ist ein positiver Hinweis auf die jeweiligen Medien-CDNs. Allgemeine Webseiten wie `youtube.com`/`netflix.com`, Browsernamen, ganze IP-Adressen und Port 443 allein erhalten keine Video-Klassifizierung.

TCP-ClientHello-Daten werden begrenzt zusammengefügt. Für QUIC v1/v2 werden nur die öffentlich ableitbaren Initial-Schlüssel verwendet, um den ClientHello zu lesen ([RFC 9001](https://www.rfc-editor.org/rfc/rfc9001.html), [RFC 9369](https://www.rfc-editor.org/rfc/rfc9369.html)). Anwendungsdaten, TLS-Sitzungsschlüssel und Medieninhalte werden nicht entschlüsselt. Eine ECH-Extension kann auch GREASE sein und wird als undurchsichtige Metadaten übersprungen. Nur ein separat öffentlich sichtbarer, spezifischer Medien-CDN-Outer-SNI kann die Verbindung klassifizieren; allgemeine Cover-Hostnamen oder eine fehlende sichtbare Medien-SNI bleiben unmarkiert. Ein verschlüsselter Inner-SNI wird nicht ermittelt oder geraten. Unbekannte QUIC-Versionen, fehlende Handshakes und beschädigte Metadaten bleiben unmarkiert. Der Cache ist auf 512 Verbindungen, 32 KiB Handshake-Daten je Transport und kurze Wiederzuordnungsfenster begrenzt.

Bei sichtbarem HTTP/1.x kann außerdem ein tatsächlicher erfolgreicher `video/*`-, HLS- oder DASH-Content-Type den konkreten Datenstrom klassifizieren; `Content-Disposition: attachment` wird ausgeschlossen. Für wechselnde verschlüsselte Hoster meldet eine entsprechend aktualisierte ELFIX-Windows-App ihre tatsächlich beobachteten Medienantworten automatisch über eine lokale Benutzer-Pipe. DYNEX prüft den tatsächlichen ELFIX-Prozess, Benutzer, EXE-Pfad und Prozessstart sowie den gemeldeten Server-Endpunkt. Es sind weder ein Konto noch eine manuelle Kopplung nötig. Beide Apps benötigen die Integration; der Programmname allein klassifiziert weiterhin kein Video. Details: [ELFIX-Medienmeldung](elfix-integration.md).

Eine verschlüsselte Dateiübertragung aus einer erkannten Medien-CDN-Verbindung lässt sich ohne Anwendungsinformation nicht sicher von Wiedergabe unterscheiden. Auch die ELFIX-Meldung gilt für den gemeldeten Server-IP-/Port-Endpunkt innerhalb der bestätigten ELFIX-Prozesse. Parallele Anfragen derselben ELFIX-Prozesse an genau diesen Endpunkt können denselben Transport teilen; einzelne verschlüsselte HTTP/2-/HTTP/3-Anfragen werden nicht getrennt markiert. Andere Anwendungen oder Serverports erhalten dadurch keine Priorität.

Gaming-/Voice-Erkennung hat Vorrang vor Video. Alle Pakete werden weiterhin sofort weitergeleitet. Video erhält einen eigenen bestätigten Paket-Zähler. Ein TCP-Neustart, neuer QUIC-Initial oder neue HTTP-Antwort kann eine vorherige Medienklassifizierung zurücksetzen.

## Aktivierung und bestätigter Zustand

Der Schalter **Paketpriorisierung aktivieren (Spiele, Sprache, Video)** startet die Priorisierung unabhängig davon, ob ein Spiel läuft oder im Vordergrund ist. Die Spielerkennung steuert weiterhin Gaming-Anzeige und Spielklassifizierung. Bei Abschaltung und beim Beenden wird die eigene Capture-Sitzung sicher freigegeben. Ein manueller Speedtest pausiert die Verarbeitung für die Messung; eine Messung ist keine Voraussetzung der Priorisierung. Nach der Diagnose wird eine weiterhin eingeschaltete Priorisierung wieder aufgenommen.

`QosActive` stammt aus dem authentifizierten Helferstatus mit frischer Prozesszuordnung. Zähler steigen erst nach erfolgreicher Weitergabe, bei ausgehenden Paketen zusätzlich nach erfolgreicher Markierung. Sie bestätigen keine bevorzugte Behandlung durch den Router. Stale Statuswerte verlieren die Aktivitätsaussage. Freigabe und Beenden benötigen die passende Bestätigung des Helfers.

## Router und Grenzen

Der Router muss DSCP auswerten, damit daraus dort eine Priorität wird. CAKE kann das mit einer passenden DiffServ-Konfiguration; Best-Effort-Konfigurationen ignorieren DSCP und verteilen weiterhin Datenströme fair. DYNEX ändert keine Routerkonfiguration, Windows-QoS-Richtlinien oder systemweiten DSCP-Registrywerte.

Es gibt weder Eingriffe in Spielprozesse noch eine bestimmte Pinggarantie. Die Filtertreiber-Kompatibilität und das tatsächliche Verhalten unter Routerlast sind separat zu prüfen.

## Alte Einstellungen

Download-/Uploadlimits, adaptive Budgets, Programmregeln und Limit-Zeitpläne werden nicht mehr ausgeführt. Der Desktop sendet keine Caps; der Helfer verwirft auch gültige alte Cap-Befehle. Selbst direkte veraltete Aufrufe von `PacketPump` können keine Drosselung aktivieren.
