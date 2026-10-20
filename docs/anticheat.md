# DYNEX und Anti-Cheat

DYNEX greift nicht in Spielprozesse ein. Eine pauschale Anti-Cheat-Kompatibilität kann nur der jeweilige Hersteller bestätigen; ein erfolgreicher isolierter Test ist keine solche Bestätigung.

## Prozess- und Paketgrenzen

- Kein Lesen oder Schreiben von Spielspeicher, keine Injektion, keine Eingabe-Hooks und kein Overlay.
- Prozessliste, Prozess-ID und Startzeit werden systemseitig abgefragt. Programmpfade benötigen höchstens die eingeschränkte Abfrageberechtigung.
- Verbindungen werden über Windows-Sockettabellen zugeordnet.
- Kein Paket wird zur Bandbreitenbegrenzung verzögert oder verworfen. Das gilt im neuen Betrieb für Spiele und Hintergrundprogramme gleichermaßen.
- Geeignete ausgehende Spielpakete erhalten **CS4**, Sprachpakete **EF** im IP-Header. Diese Prioritätsmarkierungen ändern die Paketbytes im IP-Header; bei IPv4 wird außerdem dessen Prüfsumme korrigiert. Transportheader und Nutzdaten bleiben unverändert.
- Unbekannte, mehrdeutige, veraltete und eingehende Pakete werden nicht markiert.

Frühere Hinweise, nach denen Spielpakete grundsätzlich vollständig bytegleich bleiben oder lediglich eine Downloadwarteschlange überspringen, gelten nicht für diesen reinen Priorisierungsbetrieb. Der aktive Paketpfad enthält keine solche Warteschlange mehr.

## Netzwerkfilter und Abschaltung

Der separat installierte signierte Windows Packet Filter wird weiterhin für Beobachtung und Markierung benötigt. Der reguläre Desktop läuft ohne Erhöhung; der eigene Helfer wird bei Bedarf erhöht gestartet. Watchdog, Prozessidentitätsprüfung und bestätigte Freigabe bleiben aktiv.

Der Schalter **Spiel- und Sprachpakete priorisieren** gibt die eigene Capture-Sitzung frei. Der installierte Filtertreiber bleibt dabei an die Netzwerkkarte gebunden; seine vollständige Entfernung erfolgt über die Deinstallation. Eine Garantie für alle Kernel-Anti-Cheats oder zukünftige Treiberrichtlinien gibt es nicht.

## Prüfung

`scripts/anticheat-guard.ps1` prüft verbotene Prozessspeicher-/Injektions-/Hook-/Overlay-APIs. Pakettests prüfen separat die zulässigen DSCP-Felder, IPv4-Prüfsumme sowie den Erhalt von ECN, VLAN, IPv6-Flowlabel, Transportheader und Nutzdaten. Es wurde kein Anti-Cheat-Test mit echten Spielkonten ausgeführt.
