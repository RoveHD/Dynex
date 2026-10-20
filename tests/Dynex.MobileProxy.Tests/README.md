# Lokaler SOCKS5-Datenpfad

Paketfreie, isolierte .NET-9-Konsolentests. Keine WAN-Ziele werden kontaktiert, kein VPN/Filter wird gestartet und keine Firewallkonfiguration verändert.

```powershell
dotnet run --project tests/Dynex.MobileProxy.Tests
dotnet run --project tests/Dynex.MobileProxy.Tests -- --tcp-only
dotnet run --project tests/Dynex.MobileProxy.Tests -- --require-half-close
```

Der Standardlauf enthält UDP IPv4/IPv6, zugelassene Rückwege, fremde lokale Quellen, beschädigte/fragmentierte Pakete, Nutzbytezähler und einen gemeinsamen TCP-/UDP-Downloadtest. `--tcp-only` schließt Datagrammtests ausdrücklich aus; die UDP-Steuerverbindung und ihr Abbruch bleiben enthalten. Ein direktes TCP-Socketpaar prüft vor den Halbabschluss-Assertions, ob der Host Halbabschlüsse korrekt transportiert. `--require-half-close` macht einen fehlgeschlagenen Host-Basistest zum Fehler.

## Stand der tatsächlichen Verifikation

Auf dem Entwicklungsrechner am 2026-10-16 bestanden 92 Checks mit `--tcp-only`: IPv4/IPv6 CONNECT, injizierte DNS-Auflösung, Authentifizierung, fehlerhafte Requests, vorbereitete ausgehende Sockets, zwei gemeinsam begrenzte TCP-Downloads (historisches Verhalten), unbegrenzter Upload, Limitwechsel, begrenzte Sitzungen/Warteschlange, ereignisbasierte Weiterleitungsbestätigung und Shutdown. Die Sandbox blockierte Socket-Connect mit Fehler 10013; der Test lief deshalb nach Toolfreigabe außerhalb der Sandbox.

Der vollständige Lauf auf diesem Windows-Host war durch den direkten UDP-Basistest blockiert: Ein Socket sendete an einen zweiten, bereits an 127.0.0.1 gebundenen Socket; innerhalb von drei Sekunden kam kein Datagramm an. Auch der direkte TCP-Halbabschluss ohne Proxy lieferte EOF beziehungsweise Socketfehler 10053 beim Rückkanal. Im isolierten Android-35-Emulator bestanden anschließend alle 131 Prüfungen einschließlich UDP und TCP-Half-Close mit `--require-half-close`. Android-VPN-/AdGuard-/Mobilfunk-Integration ist nicht Teil dieser Proxytests; deren gesonderter Prüfstatus steht in [docs/android.md](../../docs/android.md).

Am 2026-10-20 bestanden nach Entfernung aller Bandbreitenlimits 123 Checks mit `--tcp-only`, darunter zwei reale TCP-Downloads trotz angeforderter Altgrenze von einem Byte/s. Der volle Lauf scheiterte erneut am unveränderten direkten UDP-Host-Basistest; der Halbabschluss-Basistest blieb ebenfalls eingeschränkt. Dies ist kein Nachweis für den neuen UDP-Datenpfad auf Android.

## API und Grenzen

`LocalSocksProxy(Action<Socket> prepareOutboundSocket, int maxSessions = 128, Func<IPEndPoint, bool>? shouldLimitDownload = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null, int listenPort = 0, string? username = null, string? password = null)`

- `StartAsync(CancellationToken)`, `DisposeAsync()`, `SetDownloadLimit(long? bytesPerSecond)`; die letzte API ist nur für alte Aufrufer erhalten. Positive Werte und `null` bleiben immer unbegrenzt. `Port`, `Username`, `Password`, atomar gelesene `DownloadBytes`/`UploadBytes`, `Snapshot()` mit diesen Einzelwerten und aktiven Sitzungen.
- Ausschließlich `127.0.0.1`, zufälliger Port bei `0`; feste Ports werden ohne Ausweichport gebunden. Zufällige Zugangsdaten oder ein vollständiges, 1–255 Zeichen langes Paar aus druckbarem ASCII ohne Leerzeichen. Nur RFC1929, kein anonymer Modus.
- `prepareOutboundSocket` läuft vor jedem ausgehenden TCP-Connect/UDP-Bind/Send. Schutz- oder Netzwerkbindefehler müssen werfen; dann wird der Socket verworfen. DNS kann über den injizierten Resolver an das Android-Underlying-Network gebunden werden. Root/Android besitzt diese Plattformintegration.
- Keine Bandbreitenbegrenzung und keine Pacing-Warteschlange. Auch ein alter Aufruf mit einem Byte/s verzögert weder TCP- noch UDP-Nutzdaten. Limit-Metadaten bleiben leer; ältere Snapshot-Felder können keine aktive Begrenzung bestätigen.
- Alle Ziele werden ohne Bandbreitenlimit weitergeleitet. Die Unterscheidung öffentlicher Ziele von DNS und lokalen Netzen bleibt ausschließlich für die Telemetrie erhalten. Tests klassifizieren isolierte Loopback-Ziele als öffentlichen Verkehr, ohne WAN-Verbindungen zu öffnen.
- Höchstens 128 Sitzungen standardmäßig, konfigurierbar 1–256. Pro TCP-Richtung 16 KiB Puffer. Pro UDP-Sitzung ein lokaler Relayport, höchstens zwei ausgehende Sockets, begrenzte Empfangspuffer und höchstens 128 Rückwege mit 2 Minuten Gültigkeit seit letztem erfolgreichen Send. Kein unbeschränktes Datagramm-Queueing.
- SOCKS5-UDP authentifiziert Datagramme nicht separat. Die authentifizierte TCP-Verbindung legt die lokale IP/den Port fest; Port `0` wird beim ersten syntaktisch gültigen Datagramm festgehalten. Andere lokale Prozesse könnten diese erste Portbelegung erraten; dies ist eine Grenze des standardmäßigen SOCKS5-UDP-Protokolls. Der native Client sollte seinen Port angeben, soweit unterstützt.
- UDP-Fragmente werden verworfen, Zieladressen/-ports geprüft, SOCKS-UDP-Pakete auf 65.507 Bytes beschränkt. Antworten müssen inklusive SOCKS-Header ebenfalls in dieses Limit passen. Nur nach erfolgreichem Send werden identische Quell-IP/-Port-Rückwege zugelassen; ein gemeinsames Lock verhindert die Race Condition mit sofortigen Antworten.
- Nutzbytezähler steigen erst nach erfolgreichem Weiterleiten. Fehlgeschlagene/malformed/unautorisierte Pakete und SOCKS-Header zählen nicht. Die Snapshot-Felder sind individuell atomar, keine transaktionale Momentaufnahme beider Übertragungsrichtungen.
