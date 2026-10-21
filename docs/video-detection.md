# Videoerkennung

DYNEX für Windows ordnet nachgewiesene Videoverbindungen der Klasse **AF41 (34)** zu. Spiel- und Sprachverkehr behalten ihre eigenen Klassen; es gibt keine Bandbreitenlimits. Der Videopaketzähler bestätigt die Verarbeitung durch DYNEX, keine bevorzugte Behandlung durch den Router.

## Streamingdienste

Die Regeln verwenden den öffentlich sichtbaren Medienhostname aus dem TLS- oder QUIC-ClientHello und eine frische, eindeutige Prozess-/Socketzuordnung. Die Zuordnung gilt für die konkrete Verbindung, nicht für den gesamten Browser oder alle Verbindungen zur gleichen IP-Adresse.

| Dienst | Erkannte Medienhosts |
| --- | --- |
| YouTube | `googlevideo.com` und Unterdomains |
| Netflix | `nflxvideo.net` und Unterdomains |
| Twitch Live | `hls.ttvnw.net` und Unterdomains |
| Disney+ | `media.dssott.com` und Unterdomains |
| Prime Video | `dash.row.aiv-cdn.net`, `videorolls.row.aiv-cdn.net` und Unterdomains |
| Vimeo | `vod-adaptive-ak.vimeocdn.com`, `skyfire.vimeocdn.com` |
| Vimeo-Sendeverbindungen | `rtmp.cloud.vimeo.com`, `rtmp-global.cloud.vimeo.com`, wenn ein TLS-ClientHello sichtbar ist |
| Apple TV | `vod-ak-aoc.tv.apple.com` |
| ELFIX und sonstige Player über sichtbares HTTP | Tatsächlicher erfolgreicher Video-, HLS- oder DASH-Content-Type; Anhänge werden ausgeschlossen |
| ELFIX mit verschlüsselten/wechselnden Hostern | Automatische lokale Medienmeldung der integrierten ELFIX-Windows-App; siehe [ELFIX-Integration](elfix-integration.md) |

Anbieter-Webseiten, Suche, Chat, Anmeldungen, Vorschaubilder, Player-Skripte und allgemeine CloudFront-/Akamai-Domains sind keine positiven Videoregeln. Insbesondere sind `js-assets.aiv-cdn.net`, `f.vimeocdn.com`, `i.vimeocdn.com`, `gql.twitch.tv` und `usher.ttvnw.net` keine Belege für eine laufende Videoübertragung. Medien-CDN-Verbindungen können auch Audio, Werbung oder einen Mediendownload enthalten; verschlüsselte Einzelanfragen innerhalb desselben Transports werden nicht auseinandergetrennt.

TCP-Handshake-Segmente können innerhalb des begrenzten Puffers vertauscht eintreffen. Erst der vollständige, widerspruchsfreie ClientHello ist ein Hinweis. Überlappende Segmente müssen übereinstimmen. Fortlaufend aktive, weiterhin eindeutig zugeordnete Verbindungen behalten ihre Medienzuordnung auch nach 30 Minuten; 120 Sekunden Leerlauf, ein TCP-Neustart oder ein neuer QUIC-Initial verwerfen die alte Zuordnung. Die Zahl gespeicherter Verbindungen bleibt auf 512 begrenzt.

Die konkreten Hosts sind eine begrenzte Abdeckung bekannter Übertragungswege. Beispielsweise ist damit nicht jeder Twitch-VOD über beliebige Dritt-CDNs erfasst. Ein fehlender Handshake, ECH ohne sichtbaren Medienhostname, ein fremder VPN-Tunnel oder ein neuer Anbieter-CDN kann ohne zusätzliches Anwendungswissen unklassifiziert bleiben. Die Regeln behaupten keine vollständige Erkennung aller Streams eines Anbieters.

## Discord: Kamera und Bildschirmübertragung

Bei eindeutig zugeordneten nativen Discord-, DiscordCanary- und DiscordPTB-Prozessen beobachtet DYNEX die öffentlich sichtbaren RTP-Header. Eine kohärente Folge mehrerer Video-Frames mit gleichem SSRC und Payload-Type, passenden Sequenzen/Zeitstempeln und Frame-Abschlüssen kann Video belegen. Ein einzelnes großes UDP-Paket oder ein dynamischer Payload-Type allein genügt nicht. Die Medieninhalte bleiben verschlüsselt.

Sprach-Opus mit dem von Discord dokumentierten Payload-Type 120 bleibt Sprache. Mehrere SSRCs und Payload-Types derselben Verbindung haben getrennte Zustände: Die Kamera macht dadurch nicht das parallel übertragene Mikrofon zum Video. Kleine Pakete einer bestätigten Videofolge erhalten AF41 und werden nicht pauschal als Sprache behandelt. Ungültige Header, STUN, DTLS und RTCP begründen keinen Videotreffer.

Die konservative Frame-Prüfung braucht drei abgeschlossene Frames, darunter zwei über mehrere Pakete verteilte Frames. Sehr kleine Ein-Paket-Frames und separate RTX-Wiederholungsströme können deshalb unklassifiziert bleiben. Eine unbekannte verschlüsselte TCP-Weiterleitung oder Discord im Browser liefert diese native Appzuordnung nicht. Der Cache ist auf 1.024 RTP-Ströme begrenzt; die Evidenz verfällt nach drei Sekunden Leerlauf.

## Teams: Kamera und Bildschirmfreigabe

Für die eindeutig zugeordneten nativen Prozesse `ms-teams` und `Teams` nutzt DYNEX Microsofts dokumentierte lokale Standardportbereiche: **50020–50039** für Video und **50040–50059** für Bildschirm-/Anwendungsfreigabe. Beide erscheinen in DYNEX als Video AF41. UDP braucht zusätzlich einen gültigen RTP-Header; Kontrollpakete allein zählen nicht als Video. Microsofts App-/Quellportregeln decken auch native TCP-Übertragungen ab.

Die lokalen Ports sind entscheidend, nicht ein zufällig gleich nummerierter Serverport. Andere Anwendungen auf diesen Ports erhalten dadurch keine Videopriorität. Tenantseitig geänderte Bereiche und Browser-Teams mit dynamischen Ports werden nicht geraten. Ohne zusätzliches Medienwissen ist dort keine garantierte Videozuordnung möglich.

## Quellen und Prüfung

- [Twitch-Infrastrukturmessung, PAM 2017](https://tysong.github.io/files/PAM17.pdf): tatsächlich beobachtete HLS-Lieferhosts.
- [Common Sense Media: ursprüngliche Streaming-Netzwerkmessungen, 2021, Anhang](https://www.commonsensemedia.org/sites/default/files/research/report/privacy_of_streaming_apps_and_devices-final.pdf): Disney+-, Prime- und Apple-TV-Lieferhosts. Dies sind Messbelege, keine Zusicherung unveränderter Anbieterinfrastruktur.
- [Öffentliche Vimeo-Playerkonfiguration](https://player.vimeo.com/video/76979871/config): am 21.10.2026 ausgegebene HLS-/DASH-Lieferhosts. Zeitlich begrenzte Wiedergabe-URLs werden nicht in DYNEX gespeichert.
- [Vimeo: Netzwerkkonfiguration](https://help.vimeo.com/hc/en-us/articles/12426939452817-Recommended-network-configuration-for-live-events): RTMPS-Sendehosts und Abgrenzung von Website-Inhalten.
- [Microsoft Teams: QoS](https://learn.microsoft.com/en-us/microsoftteams/qos-in-teams), [Windows-Clientregeln](https://learn.microsoft.com/en-us/microsoftteams/qos-in-teams-clients): Appidentität, lokale Standardportbereiche und Browsergrenzen.
- [Discord: Voice-Verbindungen](https://docs.discord.com/developers/topics/voice-connections): RTP/SRTP und Opus-Payload-Type. Die konservative Video-Frame-Heuristik ist eine DYNEX-Regel, keine von Discord zugesicherte Codec-Zuordnung.

Die automatisierten Tests verwenden synthetische TLS-/QUIC-/RTP-Pakete, Protokoll-Testvektoren und isolierte Prozesszuordnungen. Sie prüfen Positivfälle, unabhängige Sprach-/Videoströme, fremde Apps, falsche Domains, TCP-Reihenfolge, Prozesswechsel, Ablauf und unveränderte Paketnutzdaten. Sie ersetzen keinen realen Videoanruf, alle Anbieterkonten oder einen Belastungstest mit dem Router.
