# DYNEX · Dynamic Network Experience

Entscheidungsstand 14.10.2026, Version 1.0.4: native Windows-WPF-App mit AutoLAN, lokaler Prozess- und Steam-Spielerkennung sowie einem separaten Helper zur Begrenzung großer Downloadverbindungen. Der Helper verwendet den unabhängig installierten Windows Packet Filter (NDISRD) direkt über Windows-IOCTLs. Das Hauptsetup übernimmt fehlende Netzwerkvoraussetzungen nach Windows-UAC-Bestätigung und dokumentiert deren Herkunft für die spätere Deinstallation; der reguläre Desktop läuft ohne Erhöhung. WinDivert wird nicht mehr verwendet. Bestehende Sicherheitsblockaden bleiben unverändert.

## LAN-Vertrauen und Kommunikation

AutoLAN ist standardmäßig für Windows-Privatnetzwerke aktiv. DYNEX entdeckt andere laufende DYNEX-PCs automatisch; ein Gruppencode ist zum Start nicht erforderlich. Die erste neue Peer-Identität im privaten LAN wird automatisch vertraut. Das ist eine bewusste Vertrauensannahme für dieses LAN: Geräte, denen das private Netzwerk zugänglich ist, können beim ersten Kontakt in die DYNEX-Kommunikation aufgenommen werden. Wer eine bewusst abgegrenzte gemeinsame Gruppe möchte, kann optional weiterhin eine private Gruppe verwenden.

Die Peer-Kommunikation verwendet ECDSA zur Identitäts-/Signaturprüfung, ECDH zur Ableitung eines Schlüssels pro Peer und AES-GCM für authentifizierte Verschlüsselung. Sie basiert nicht auf einem globalen, für alle Peers gemeinsam verwendeten Gruppenschlüssel. Konkrete Paketfelder und Replay-Schutz sind in [protocol.md](protocol.md) beschrieben. Kommunikation und Discovery ändern keine Systemregeln. Das Setup richtet nach Windows-Bestätigung eine eigene anwendungsspezifische Firewallfreigabe für UDP 39274 im privaten lokalen Subnetz ein; Routerkonfiguration und Netzwerkprofil bleiben erhalten.

## Budget und Spielstatus

Das Standard-Hintergrundbudget beträgt 65 % der gemessenen Anschlusskapazität. Für jeden erkannten Gaming-PC wird automatisch eine Reserve von `min(10 Mbit/s, 5 % der Kapazität)` berücksichtigt. Ab 0.3.1 übertragen die PCs ihre Kapazitäts- und Reservevorschläge mit ihren authentifizierten Statusmeldungen. Der kleinste vollständige Vorschlag bestimmt den gemeinsamen Topf; dieser wird unter allen teilnehmenden PCs aufgeteilt, einschließlich Gaming-PCs. Dadurch werden Steam-Downloads auf dem eigenen Gaming-PC ebenfalls berücksichtigt. Unterschiedliche Messwerte bleiben zulässig. Die Mindestbildung wird immer aus den ursprünglichen lokalen Vorschlägen berechnet, damit ein abgelaufener langsamer Peer keinen dauerhaft zirkulierenden Mindestwert hinterlässt.

Die adaptive Pingreserve ist immer eingeschaltet und wird nicht als UI-Schalter angeboten. Sie kann eine gültige Idle-Baseline des Gaming-Peers verwenden, um das Downloadbudget am anderen PC anzupassen. Eine Anpassung ersetzt keine Messung des Spielserverpfads und ist keine Zusage für 30 ms oder eine bestimmte Verbesserung.

Die Spielersuche umfasst Prozessnamen bekannter Spiele, benutzerdefinierte Namen und lokal installierte Steam-Spiele aus den gefundenen Steam-Bibliotheken. Steam-Pfadtreffer benötigen zusätzlich die positive Typangabe `Game` aus dem lokalen Steam-Appcache. Software, Tools und unbekannte Typen werden nicht automatisch gewertet. Wallpaper Engine wird zusätzlich gezielt ausgeschlossen, auch wenn die Steam-Metadaten fehlen. Details und Fehlergrenzen stehen in [Spielerkennung](game-detection.md).

Eine Übereinstimmung bedeutet nur, dass ein Spielprozess läuft. Der Prozess kann sich im Menü befinden; DYNEX bestätigt weder Multiplayer noch Match, Serververbindung oder aktive Runde. Ein Spielstart darf als Fallback verwendet werden.

## Shaping und Systemgrenze

`Dynex.Core` verwaltet Peerstatus und Budgetplanung; `Dynex.Desktop` stellt Oberfläche, Discovery, Messungen, Einstellungen und Prozesserkennung bereit. `Dynex.Shaper` liest Ethernetframes, ordnet TCP/UDP-Verbindungen über frische Windows-Sockettabellen Prozessen zu und lässt Spiele, Kommunikationsprogramme und kleine Verbindungen direkt passieren. Erst anhaltend große Transfers erhalten ein gemeinsames Bytebudget. Prozess-ID und Startzeit der erkannten Spiele werden über eine beidseitig gegen Prozessidentitäten geprüfte lokale Pipe übertragen. Ohne aktive Filterverbindung und frische Prozesszuordnung wird kein aktives Limit gemeldet. Die reale NDIS-/WAN-Abnahme ist separat zu prüfen; automatisierte Tests alleine bestätigen keine Pingverbesserung.

Auch mit einem funktionierenden Paketfilter kommen eingehende Pakete bereits über den Internetanschluss. Verzögerung kann TCP und congestion-controlled QUIC beeinflussen, garantiert aber weder weniger WAN-Verbrauch bei beliebigem UDP noch eine konstante Pingverbesserung. Andere PCs ohne DYNEX, WLAN-Störungen, Upload-Sättigung, Providerprobleme, VPNs und Filtertreiber bleiben außerhalb der Kontrolle. Es gibt keine Prozessspeicher-Manipulation oder Anti-Cheat-Umgehung.

Die zugrunde liegende Machbarkeit und Windows-QoS-Abgrenzung stehen in [Shaping](shaping.md); Details zum Peer-Protokoll stehen in [protocol.md](protocol.md).
