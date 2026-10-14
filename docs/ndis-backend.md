# Windows Packet Filter als unabhängiger Backend

Der NDIS-Backend verwendet **Windows Packet Filter / WinpkFilter (`ndisrd.sys`) von NT KERNEL**, einen anderen Treiber als WinDivert. Die Herstellerseite bietet [Version 3.6.2.1 für Windows x64](https://www.ntkernel.com/windows-packet-filter/download/) als Microsoft-attestierten NDIS-6-Lightweight-Filter für unter anderem Windows 10/11 an. Das ist eine Herstellerangabe; die tatsächliche Dateisignatur und Lauffähigkeit müssen am konkreten Paket/Zielsystem geprüft werden. Bestehende Sicherheitsblockaden werden weder umgangen noch durch Umverpacken eines blockierten Treibers behandelt.

## Bezug und Rechte

Die Hersteller-Downloadseite verweist auf das [offizielle GitHub-Release v3.6.2](https://github.com/wiresock/ndisapi/releases/tag/v3.6.2). Die Release-API gab für `Windows.Packet.Filter.3.6.2.1.x64.msi` am 14.10.2026 diesen Digest zurück:

```text
sha256:9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c
```

Der regulär installierte Treiber ist eine Voraussetzung dieses Backends. Seit 1.0.2 übernimmt das DYNEX-Setup bei fehlendem Filter den direkten, geprüften Download vom Hersteller und startet die offizielle MSI-Einrichtung mit Windows-Bestätigung. Der Backend selbst installiert keinen Treiber. Die Installation kann Netzwerkbindungen vorübergehend unterbrechen. Das MSI wird nicht im DYNEX-Paket weiterverteilt.

Die [Herstellerbedingungen](https://www.ntkernel.com/windows-packet-filter/licensing/) unterscheiden kostenlose persönliche Nutzung/Evaluation von kommerzieller Weitergabe. Die kommerzielle Treiberweitergabe erfordert eine gesonderte Lizenz. Die [MIT-Lizenz der User-Mode-NDISAPI-Bibliothek](https://github.com/wiresock/ndisapi/blob/v3.6.2/LICENSE) macht den proprietären Kerneltreiber nicht frei weiterverteilbar. Der öffentliche Treiber unterstützt Standard-MTU bis 1500 Byte; Jumbo-Frames erfordern laut Hersteller eine kommerzielle Variante. Ein eigener signierter Custom-Build/isolierter Gerätenamensraum ist kein Bestandteil der kostenlosen Einrichtung.

## Tatsächliche Schnittstelle

`NdisPacketDevice` implementiert `IPacketDevice.Receive/Send/Dispose`. Es benötigt **keine `ndisapi.dll`**: Der begrenzte User-Mode-Wrapper wird direkt über Windows `CreateFileW` und `DeviceIoControl` abgebildet. Die offizielle Quelle ist [Common.h v3.6.2](https://github.com/wiresock/ndisapi/blob/v3.6.2/include/Common.h), abgeglichen mit den Datenrichtungen in [ndisapi.cpp v3.6.2](https://github.com/wiresock/ndisapi/blob/v3.6.2/ndisapi/ndisapi.cpp). Alle Aufrufe verwenden den regulär installierten Gerätenamen `\\.\NDISRD`; es wird kein eigener Kernelcode gebaut.

`CTL_CODE(0x8300, 0x830 + Index, METHOD_BUFFERED, FILE_ANY_ACCESS)`:

| Index | Offizielle Operation | Verwendung |
|---|---|---|
| 0 | `GET_VERSION` | Treiber verfügbar und ABI-Version `0x06023000` (3.6.2) |
| 1 | `GET_TCPIP_INTERFACES` | Gebundene Adapter abfragen |
| 2 | `SEND_PACKET_TO_ADAPTER` | Ausgehenden Frame nach optionaler DSCP-Markierung an den Adapter geben |
| 3 | `SEND_PACKET_TO_MSTCP` | Eingehenden Frame dem TCP/IP-Stack zustellen |
| 4 | `READ_PACKET` | Frame lesen und originale Sende-/Empfangsrichtung bewahren |
| 5 | `SET_ADAPTER_MODE` | Modus 2 für reine Downloads, Modus 3 für bidirektionales QoS; bei Abschaltung zurück auf 0 |
| 6 | `FLUSH_ADAPTER_QUEUE` | Nur die zuvor eigene Capture-Queue nach erfolgreichem Reset leeren |
| 7 | `SET_EVENT` | Paketereignis registrieren/abmelden |
| 12 | `ADAPTER_QUEUE_SIZE` | Vor einem nicht wartenden Read vorhandene Pakete prüfen |
| 13 | `GET_ADAPTER_MODE` | Fremdbelegung und spätere Modusänderung erkennen |
| 16 | `GET_PACKET_FILTERS_TABLESIZE` | Bereits vorhandene globale Filter erkennen |

Die ABI ist bewusst auf Windows x64 und die belegte API-Version 3.6.2 begrenzt. Ein späterer unbekannter Treiber wird bis zum Abgleich seiner ABI abgelehnt. `#pragma pack(1)` ergibt 12 Byte `ADAPTER_MODE`, 16 Byte `ETH_REQUEST`, 52 Byte Frame-Metadaten, 1566 Byte Standard-`INTERMEDIATE_BUFFER` und 8836 Byte `TCP_AdapterList`. `ValidateAbi` und `GetAbiSizes` sind ohne geladenen Treiber prüfbar. Die Metadaten enthalten originale NDIS-/VLAN-Felder; sie werden zusammen mit dem vollständigen originalen Ethernet-Frame bewahrt.

Der Backend beansprucht ausschließlich aktive, Windows-seitig als Ethernet/WLAN eingeordnete Adapter mit Gateway und von NDIS als Ethernet (`NdisMedium802_3`) gemeldeten Frames. Raw-IP-, WAN- und native 802.11-Medien werden nicht anhand eines geratenen Ethernet-Headers behandelt. Andere Routen/VPN-Tunnel und Spezialadapter benötigen eigene Kompatibilitätsprüfung. Die Windows-Schnittstellen und Adapterhandles werden beim Erzeugen der Sitzung ermittelt; ein Netzwechsel muss die Sitzung beenden und neu ermitteln.

`Receive` liefert `CapturedPacket.Buffer/Length` als vollständigen Ethernet-Frame und setzt `IpOffset` auf 14, 18 oder 22 für IPv4/IPv6 mit bis zu zwei 802.1Q/802.1ad-Tags. ARP, unbekannte EtherTypes, unvollständige Header und weitere Tags erhalten `IpOffset = -1`. Das ist ein Bypasshinweis, kein Grund zum Verwerfen. Die Ownership-/Limitpolicy liegt außerhalb des Geräts; sie muss Spiele, unbekannte/mehrdeutige Besitzer, lokale Protokolle und nicht unterstützte Pakete sofort unverändert senden. Nur positiv zugeordneter, ausgewählter Downloadtraffic darf in die zeitgesteuerte Queue gelangen.

Ausgehende Frames sind seit 0.4.0 ebenfalls unterstützt. `CapturedPacket.Direction` wird aus dem originalen `DeviceFlags` abgeleitet; beim Senden entscheidet erneut das unveränderte native Metadatenfeld über IOCTL 2 oder 3. Ausgehende Pakete geraten nie in die Downloadqueue. Nur die DSCP-Markierung und nötige IPv4-Headerprüfsumme werden bei positiv erkanntem Echtzeitverkehr geändert; siehe [QoS](qos.md).

## Prozesszuordnung und Grenzen

NDIS-Ethernetframes enthalten keine Prozess-ID. Der [Hersteller-Prozesslookup](https://github.com/wiresock/ndisapi/blob/v3.6.2/examples/cpp/common/iphelper/process_lookup.h) demonstriert `GetExtendedTcpTable` und `GetExtendedUdpTable` für IPv4/IPv6. TCP lässt sich über lokale/entfernte Endpunkte zuordnen. UDP-Tabellen liefern lokale Bindungen; mehrere Prozesse mit gemeinsamem Port, kurze Flows, Tabellenverzögerung, PID-Wiederverwendung und Fragmente erlauben keine pauschale vollständige Zuordnung. Unbekannte/mehrdeutige Flows müssen frei bleiben, damit Spieltraffic nicht versehentlich gedrosselt wird. Die separate Ownership-Komponente muss frische Snapshots und Prozessstartzeit prüfen.

Wie jedes lokale Ingress-Shaping steuert dies die Zustellung auf diesem PC. TCP-/QUIC-Sender können darauf reagieren; bereits eingetroffener unkooperativer UDP-Traffic hat die WAN-Leitung schon passiert. Das ist keine garantierte Bandbreitenreservierung im Router und keine Garantie eines bestimmten Spielpings.

## Lebenszyklus und Koexistenz

Der IPC-Server läuft im regulär gestarteten DYNEX-Prozess mit `CurrentUserOnly`-ACL; nur der Helfer wird erhöht. Der Helfer prüft die Benutzer-SID des Parent-Prozesstokens, Parent-PID und Startzeit, die tatsächliche Pipe-Server-PID sowie den Pipe-Eigentümer gegen seine Benutzer-SID. Die .NET-Clientoption `CurrentUserOnly` ist hier ungeeignet: [die .NET-9-Implementierung](https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.Windows.cs) vergleicht den Pipe-Eigentümer mit `WindowsIdentity.Owner`, der beim erhöhten Token die Administratorengruppe sein kann. Die Prüfung geschieht erst nach dem Betriebssystem-Connect und kann daher einen verbundenen, aber ohne Status abbrechenden Helfer verursachen. Die explizite SID-Prüfung ersetzt diese Clientprüfung ohne Änderung der Server-ACL. Der Client erlaubt dem Server keine Impersonation. Ein erhöht gestarteter Desktop mit Administratorengruppe als Pipe-Eigentümer wird abgewiesen; der Desktop ist regulär zu starten. Nach authentifiziertem Connect werden Initialisierungs-/Laufzeitfehler als inaktive JSON-Statusmeldung mit Ausnahmetyp und HRESULT gemeldet; Stacktraces, Prozesslisten und Netzwerkadressen werden nicht übertragen.

Ein benanntes Semaphore verhindert mehrere eigene DYNEX-Captures, ohne eine threadgebundene Mutex-Ownership über asynchrone Lebenszyklen zu tragen. `SafeFileHandle` plus gemeinsames Gate schützen native Aufrufe, Bufferspeicher und Disposal vor konkurrierender Benutzung. Ein wartender Receive verwendet registrierte Paketereignisse; die Abschaltung signalisiert sein Stop-Ereignis und gibt Wartesignale erst nach Verlassen eines aktiven Receives frei. Native Aufrufe laufen synchron; ein fehlerhaft hängender Kernel-IOCTL kann nicht als bereits erfolgreich gestoppt gemeldet werden.

Adaptermodi, Filtertabellen und der Standard-Treiber sind **nicht isoliert pro DYNEX-Handle**. Vor jeder Mutation müssen alle vorgefundenen Modi 0 und die globale statische Filtertabelle leer sein. Der vorhandene Registry-`StartupMode` wird nur gelesen und muss ebenfalls 0 sein. DYNEX verändert weder statische Filtertabellen noch globale Startmodi, MTU, Hardwarefilter oder Registrykonfiguration. Bei Fehlern während der Einrichtung werden bereits eigene Adapter zurückgesetzt. Beim Dispose werden nur nachweislich eigene Empfangsmodi zurückgesetzt, Ereignisse abgemeldet, die eigenen Queues geleert und der Gerätehandle geschlossen. Ein unerwartet fremder Modus wird nicht überschrieben; `CleanupError` macht fehlgeschlagene Freigabe sichtbar.

Eine Prüfung auf Modus 0 kann einen anderen nur beobachtenden/inaktiven Client nicht beweisen ausschließen; ein konkurrierender Fremdclient könnte auch nach der Prüfung starten. Laut [Herstellerantwort](https://www.ntkernel.com/forums/topic/install-winpkfilter-3-0-on-xen-vps/) erfolgt der automatische Treiberreset beim Ende **aller** User-Mode-Clients, nicht bereits garantiert beim Schließen eines beliebigen Handles. Daher wird kein uneingeschränktes Crash-Fail-open bei gleichzeitiger Fremdnutzung behauptet. Der Hersteller empfiehlt für sichere Koexistenz einen [isolierten Custom-Build](https://www.ntkernel.com/windows-packet-filter/licensing/). Der Standard-Backend darf nicht gleichzeitig mit anderen WinpkFilter-Programmen verwendet werden. Normaler Stopp, Prozessabbruch und Wiederfreigabe müssen auf der konkret installierten Version getestet werden.

## Abnahmekriterium

Vor „Begrenzung aktiv“ sind echte Prüfungen nötig: offizieller MSI-Digest und Windows-Kernelsignatur unverändert gültig, regulärer Treiberstart ohne Sicherheitsausnahmen, ABI-Version/Adapterliste plausibel, unverändertes IPv4-/IPv6-/VLAN-Passthrough, gleichzeitig selektiv begrenzter TCP-/UDP-Download und freier Spiel-/DNS-/LAN-Verkehr, kontrollierte UDP-Ambiguität sowie sofortige Wiederfreigabe bei normalem Stopp, Parent-/Heartbeatverlust, Helper-Kill und Netzwechsel. Adaptermodus muss danach 0 sein. Bereits aktive Fremdmodi und statische Filter müssen zum Abbruch führen, ohne deren Zustand zu verändern; passive Fremdclients sind dagegen nicht zuverlässig ausschließbar. Loopback-/Fake-Device-Tests allein beweisen das nicht.

Der Helper-Build wurde ohne Warnungen/Fehler ausgeführt; die reine ABI-Prüfung lieferte `(12, 16, 52, 1566, 8836)`. Diese Datei beschreibt den implementierten I/O-Baustein und seine Grenzen. Der Baustein hat während seiner Entwicklung keinen Treiber installiert, keinen Netzwerkadapter aktiviert und keine realen Pakete abgefangen. Die native Abnahme ist ein gesonderter, ausdrücklich zu koordinierender Schritt.

## Freigabe nach einem Adapterwechsel (1.0.2)

Windows kann bei Filterinstallation, Rebind oder Adapterwechsel eine bisherige Adapterkennung verwerfen. Meldet ein Cleanup-IOCTL Fehler 87, liest DYNEX einmal eine neue NDIS-Adapterliste. Nur wenn die erfolgreiche, vollständig geprüfte Handle-Liste die alte Kennung nicht enthält, gilt deren Freigabe als erledigt. Ein Ersatzadapter wird dabei weder zurückgesetzt noch übernommen. Ein weiterhin vorhandenes Handle, eine unvollständige Liste oder ein weiterer Fehler bleiben sichtbar und verhindern eine unbestätigte Wiederaktivierung. Die dokumentierte 12-Byte-Struktur von GET_ADAPTER_MODE bleibt unverändert. Neue Handles werden beim nächsten bewussten Aktivieren oder App-Start ermittelt.