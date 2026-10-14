# Priorisierung von Spielen und Sprachchat

Ab DYNEX 0.4.0 betreibt der eingeschaltete Netzwerkschutz zusätzlich zur Downloadbegrenzung eine eigene QoS-Sitzung. Sie bleibt auch ohne aktives Spiel und ohne Downloadlimit eingeschaltet, damit erkannter Sprachchat priorisiert werden kann. Ausschalten, Speedtest, Überwachungsfehler und Beenden geben den Filter wieder frei. Der Desktop verlangt eine ausdrückliche Abschaltbestätigung des Helpers.

## Was tatsächlich passiert

Erkannte Echtzeitpakete umgehen die lokale Downloadwarteschlange. Ausgehende Pakete erhalten zusätzlich eine DSCP-Markierung im IPv4-/IPv6-Header: **CS4 (32)** für interaktiven Spielverkehr und **EF (46)** für erkannten Sprachverkehr. Das folgt den Klassen für Real-Time Interactive und Telephony in [RFC 4594](https://www.rfc-editor.org/rfc/rfc4594.html). Die ECN-Bits bleiben erhalten; bei IPv4 wird die Headerprüfsumme korrigiert. IPv6-Flowlabel, Transportprüfsummen und Nutzdaten bleiben unverändert.

Der NDIS-Adapter arbeitet dafür im Sende- und Empfangstunnel. Empfangene Frames werden dem TCP/IP-Stack zurückgegeben, ausgehende dem Netzwerkadapter. Die Richtung stammt aus den originalen NDIS-Metadaten; die Implementierung verwendet die dokumentierten Operationen [SetAdapterMode](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/setadaptermode/), [SendPacketToAdapter](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/sendpackettoadapter/) und [SendPacketToMstcp](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/sendpackettomstcp/). Es werden keine Windows-QoS-Richtlinien, Routereinstellungen oder systemweiten DSCP-Registrywerte geändert.

Die Oberfläche unterscheidet bestätigte Downloadbegrenzung und aktive Priorisierung. Die Zähler erfassen erfolgreich lokal weitergegebene priorisierte Pakete in der aktuellen Filtersitzung. Ab 0.4.1 erhalten Limitänderungen und der Wechsel zwischen Limit und reiner Priorisierung die Sitzung samt Warteschlange und Zählern; ein Neustart der Filtersitzung setzt die Zähler zurück. Eingehende Echtzeitpakete zählen für die lokale bevorzugte Weitergabe, ausgehende erst nach erfolgreicher Markierung und Weitergabe. Sie sind **keine Bestätigung einer bevorzugten Behandlung durch den Router**.

## Erkennung und Grenzen

- Spiele werden durch Prozess-ID und passende Prozessstartzeit zugeordnet. Kleine TCP-Nutzdaten bis 768 Byte und Spiel-UDP bis 1.400 Byte erhalten CS4; große TCP-Assetübertragungen werden nicht pauschal als Echtzeit markiert. Ein Spielstart ist kein Nachweis einer aktiven Mehrspielerrunde.
- Bei bekannten Sprachprogrammen werden kleine UDP-Pakete bis 600 Byte priorisiert. EF ist pro Prozessidentität und Richtung auf 64 KiB/s mit 4 KiB Startguthaben begrenzt. Große Dateiübertragungen und Videopakete bekommen deshalb nicht allein aufgrund des Appnamens diese Markierung.
- Für andere Programme erkennt DYNEX konservativ fortlaufende RTP-Audiofolgen mit festen Payloadtypen 0, 8 und 18. Vier plausible Pakete mit passendem SSRC, Sequenz, Zeitstempel und Abstand sind erforderlich. Die festen Zuordnungen stammen aus [RFC 3551](https://www.rfc-editor.org/rfc/rfc3551.html).
- Dynamische Payloadtypen wie Opus in unbekannten WebRTC-Apps können ohne die zugehörige Aushandlung nicht zuverlässig als Sprache erkannt werden. DYNEX entschlüsselt keine Gespräche und speichert keine Sprachinhalte. Unbekannte Browserpakete bekommen keine pauschale Priorität.
- Bei fehlender, mehrdeutiger oder veralteter Prozesszuordnung bleibt Verkehr unverändert und ungedrosselt. DNS und Discovery bekommen keine Spiel-/Sprachmarkierung.

DSCP ist eine Klassifizierung für nachfolgende Geräte. Router, WLAN und Provider müssen diese Klassen unterstützen und entsprechend konfigurieren; sie dürfen Markierungen auch ignorieren oder verändern. DYNEX kann bereits aufgelaufene Routerwarteschlangen, Uploads anderer Anwendungen und die native Adapter-FIFO nicht rückwirkend umordnen. Die Downloadbudgets bleiben daher der lokale Mechanismus gegen große Hintergrunddownloads. Es gibt keine bestimmte Pinggarantie.

## Prüfung und Rückweg

Die Pakettests prüfen IPv4-Prüfsumme, IPv6-DS-Feld, ECN, Richtungen, Prozessidentität, RTP-Fehlfälle, Begrenzung der Sprachklasse, Downloadqueue-Bypass und den Lebenszyklus ohne Downloadlimit. Der explizite native Test `tests/Dynex.Live.Tests --qos` prüft zusätzlich den echten Filter mit einem begrenzten Download, 1 MB synthetischem Upload und 80 synthetischen RTP-Stillepaketen an einen lokalen Testport; Ergebnisse stehen in den jeweils erzeugten QA-Artefakten. Das ersetzt keinen Test echter Gespräche oder mehrerer physischer PCs.

Über den Netzwerkschutz-Schalter lässt sich die Priorisierung zusammen mit der Downloadbegrenzung ausschalten. Beim vollständigen Beenden von DYNEX werden die zuvor eigenen Adaptermodi zurückgesetzt. Der separat installierte Treiber bleibt installiert; seine Deinstallation erfolgt bei Bedarf über die Windows-Appverwaltung.
