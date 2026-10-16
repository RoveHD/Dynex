# Bekannte Geräte und gezielter Verbindungstest

AutoLAN bleibt standardmäßig aktiv. Unter „Geräte“ bleiben authentifiziert beobachtete Geräte auch offline sichtbar. Gerätename, letzte bestätigte Sichtung, Adresse und UDP-Port werden lokal in `devices.json` gespeichert. Ein Alias ändert nur die lokale Anzeige; die Identität bleibt dieselbe. „Ignorieren“ und „Sperren“ schließen die betreffende ID auf diesem PC aus Empfang, Handshake, aktiven Rückrouten und gemeinsamer Budgetplanung aus. Eine lokale Sperre ist keine zentrale Routerregel und kann andere DYNEX-Installationen nicht konfigurieren.

Die Liste wird bei erster Sichtung und expliziter Änderung atomar gespeichert, laufende Sichtungen spätestens nach 30 Sekunden und beim ordentlichen Stop. Nach einem abrupten Prozessabbruch kann die letzte Sichtung entsprechend älter sein. Nach einem Neustart gelten alle Einträge zunächst als offline, bis ein frischer authentifizierter Status empfangen wird. Eine unlesbare oder beschädigte Geräteliste wird nicht überschrieben: Die App startet, der Peer-Netzwerkverkehr bleibt gesperrt und die Fehlermeldung nennt die betroffene Datei. Datei aus einer vertrauenswürdigen Sicherung reparieren und DYNEX neu starten. Das Löschen der Datei würde auch die lokalen Ausschlüsse entfernen.

„Verbindung testen“ prüft genau das gewählte Gerät und dessen zuletzt authentifizierten Endpunkt. Es findet kein neuer LAN-Scan statt. Das Ergebnis zeigt getrennt:

- Lokale, ausschließlich lesende Firewall- und Netzdiagnose (bis 10 Sekunden). Konfigurationsnachweise und unbekannte Windows-Befunde werden als solche ausgewiesen.
- Aktive Anfrage und authentifizierte Antwort vom ausgewählten Gerät.
- Gegenprobe: Die Gegenstelle bestätigt unsere Antwort auf ihre neue Challenge.
- Geräteauthentifizierung sowie angekündigte DYNEX-Version.
- Nutzbaren UDP-Pfad zur Gegenstelle; ihre Firewallkonfiguration wird nicht ausgelesen.

Die aktive Probe ist auf fünf Sekunden begrenzt und abbrechbar. Offline-Geräte werden über ihren gespeicherten Endpunkt aktiv geprüft. Eine inzwischen anders vergebene IP kann wegen der Bindung an Geräte-ID, frischen Nonce, Challenge und Quell-Endpunkt keinen falschen Erfolg liefern. Ohne passende Antwort bleiben Erreichbarkeit, Gegenstellen-Version und Ursache unbekannt. Ältere DYNEX-Versionen ignorieren den neuen Nachrichtentyp; aus ihrem Schweigen wird keine bestimmte Inkompatibilität abgeleitet.

## Protokoll und Sicherheitsgrenzen

`DYNP` Version 1 ist ein eigener, begrenzter Nachrichtentyp am bereits bestehenden UDP-Socket. Er verändert das Heartbeat-Protokoll nicht und lernt keine Statusrouten oder Budgetteilnehmer. AutoLAN-Proben verwenden ECDSA P-256, wobei die Geräte-ID vom öffentlichen Identitätsschlüssel abgeleitet wird. Privatgruppen verwenden HMAC-SHA256 mit dem bestehenden Gruppenschlüssel; wie beim bestehenden Privatgruppenstatus besitzen Gruppenmitglieder diesen gemeinsamen Schlüssel und können dadurch Identitäten innerhalb ihrer Gruppe behaupten.

Vier Nachrichten binden Sender/Ziel, zufälligen Test-Nonce, frische Gegen-Challenge, Typ, Version und Zeitstempel an ihre Authentifizierung. Der Empfänger prüft erlaubten Netzbereich, Zielidentität, Frische (30 Sekunden), Endpunkt und Ausschlüsse. Wartende Tests sind auf vier, Gegen-Challenges auf 64 und empfangene Probe-Pakete auf 64 pro Sekunde begrenzt. Verlorene UDP-Nachrichten werden innerhalb desselben festen Testzeitlimits mit unverändertem Challenge-Zustand wiederholt. Doppelte Anfragen ändern weder Challenge noch Ablaufzeit; abgeschlossene Anfragen bleiben bis zur Ablaufzeit stillgelegt, doppelte Bestätigungen dürfen lediglich die bereits feststehende letzte Antwort wiederholen.

Konfigurationsdiagnosen sind zusätzliche Hinweise und können unbekannt bleiben, während der konkrete UDP-Pfad vollständig nachgewiesen ist. Der Gesamterfolg verlangt alle eigentlichen Probe-, Authentifizierungs- und Versionsstufen. Er bestätigt keine globale Firewallkonfiguration und keine Funktionsfähigkeit in anderen Netzwerken.

## Ausführbare Prüfungen

```powershell
dotnet run --project tests/Dynex.Devices.Tests/Dynex.Devices.Tests.csproj
dotnet run --project tests/Dynex.DeviceRuntime.Tests/Dynex.DeviceRuntime.Tests.csproj
```

Die Tests verwenden dynamische Loopback-Ports und temporäre Profile. Geprüft werden beide Netzwerkmodi, Erfolg, Timeout, Abbruch, gespeicherte Offline-Endpunkte, falsche signierte Gegenstelle mit korrektem Nonce/Endpunkt, Version unbekannt/abweichend, Replay, deterministischer Verlust jeder der vier Nachrichten, unmittelbare Sperre, Entsperren mit erhaltenem Replayzustand, Alias/Offline-Persistenz sowie Appstart und Shutdown mit beschädigter Geräteliste. In einer Windows-AppContainer-Sandbox kann Loopback-UDP beziehungsweise atomarer DPAPI-Dateiersatz gesperrt sein; dafür ist die normale lokale Testumgebung erforderlich. Physische Zwei-PC-/WLAN-/Routertests sind dadurch nicht ersetzt.
