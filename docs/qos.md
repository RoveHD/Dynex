# Spiel- und Sprachpaketpriorisierung

DYNEX arbeitet ausschließlich mit Paketklassifizierung und direkter Weiterleitung. Es gibt keine Download-/Uploadwarteschlange, keinen Pacer und kein gemeinsames Bandbreitenbudget im aktiven Paketpfad.

## Markierungen

Ein frischer und eindeutiger Prozess-/Sockettreffer ordnet Pakete einem erkannten Spiel oder Sprachprogramm zu. Kleine interaktive Spiel-TCP-Pakete bis 768 Byte Nutzdaten und Spiel-UDP bis 1.400 Byte sind für **CS4 (32)** geeignet. Große TCP-Assetübertragungen erhalten keine pauschale Spielpriorität. Sprach-UDP ist bis 600 Byte geeignet; konservativ erkannte RTP-Audiofolgen können auch ohne bekannten Programmnamen berücksichtigt werden. Die Sprachklasse hat einen begrenzten Markierungsanteil: Überschreitende Pakete werden normal weitergeleitet, nicht gedrosselt.

Nur ausgehende Pakete werden markiert. IPv4 verwendet das DS-Feld und eine korrigierte IP-Headerprüfsumme; IPv6 das Traffic-Class-Feld. ECN, IPv6-Flowlabel, Transportheader und Nutzdaten bleiben erhalten. Eingehende Antworten übernehmen die ausgehende Markierung nicht automatisch. Die Klassen orientieren sich an [RFC 4594](https://www.rfc-editor.org/rfc/rfc4594.html).

## Aktivierung und bestätigter Zustand

Ein geeignetes Spiel auf diesem oder einem verbundenen DYNEX-PC muss bestätigten Netzwerkverkehr haben. Ohne diesen Verkehr bleibt gegebenenfalls eine passive lokale Beobachtung aktiv. Ohne Bedarf, bei Abschaltung oder beim Beenden wird die eigene Capture-Sitzung sicher freigegeben. Sprachchat allein aktiviert die Sitzung nicht. Ein manueller Speedtest pausiert die Verarbeitung für die Messung; eine Messung ist keine Voraussetzung der Priorisierung.

`QosActive` stammt aus dem authentifizierten Helferstatus mit frischer Prozesszuordnung. Zähler steigen erst nach erfolgreicher Weitergabe, bei ausgehenden Paketen zusätzlich nach erfolgreicher Markierung. Sie bestätigen keine bevorzugte Behandlung durch den Router. Stale Statuswerte verlieren die Aktivitätsaussage. Freigabe und Beenden benötigen die passende Bestätigung des Helfers.

## Router und Grenzen

Der Router muss DSCP auswerten, damit daraus dort eine Priorität wird. CAKE kann das mit einer passenden DiffServ-Konfiguration; Best-Effort-Konfigurationen ignorieren DSCP und verteilen weiterhin Datenströme fair. DYNEX ändert keine Routerkonfiguration, Windows-QoS-Richtlinien oder systemweiten DSCP-Registrywerte.

Es gibt weder Eingriffe in Spielprozesse noch eine bestimmte Pinggarantie. Die Filtertreiber-Kompatibilität und das tatsächliche Verhalten unter Routerlast sind separat zu prüfen.

## Alte Einstellungen

Download-/Uploadlimits, adaptive Budgets, Programmregeln und Limit-Zeitpläne werden nicht mehr ausgeführt. Der Desktop sendet keine Caps; der Helfer verwirft auch gültige alte Cap-Befehle. Selbst direkte veraltete Aufrufe von `PacketPump` können keine Drosselung aktivieren.
