# DYNEX und Anti-Cheat

DYNEX ist so gebaut, dass Spiele und ihre Anti-Cheat-Systeme (z. B. Riot Vanguard, Easy Anti-Cheat, BattlEye, VAC, FACEIT, Ricochet) nichts sehen, was wie ein Cheat aussieht. Eine Garantie kann niemand außer dem jeweiligen Hersteller geben; die folgenden Regeln sind aber technisch durchgesetzt und werden bei jedem Push geprüft.

## Was DYNEX niemals tut

- **Kein Zugriff auf Spielspeicher:** kein Lesen oder Schreiben von Prozessspeicher, keine Modulliste (`ReadProcessMemory`, `WriteProcessMemory`, `PROCESS_VM_*`, `.Modules`).
- **Keine Code-Injektion:** keine Remote-Threads, keine DLLs im Spielprozess, keine APCs.
- **Keine Eingabe- oder Fenster-Hooks:** kein `SetWindowsHookEx`, `SetWinEventHook`, `SendInput`; DYNEX liest nur, welches Fenster im Vordergrund ist.
- **Kein Overlay:** DYNEX zeichnet nicht über Spiele. Status gibt es im Infobereich und in Windows-Benachrichtigungen.
- **Keine verzögerten oder verworfenen Spielpakete:** Pakete erkannter Spiele umgehen Download- und Upload-Warteschlange immer (`PacketPump`, `BulkDownloadPolicy.ShouldLimitUpload`). Ein „Lag-Switch“-Muster entsteht dadurch nicht. Begrenzt werden nur große Downloads anderer Programme.
- **Spielpakete bleiben unverändert (ab 1.0.6 Standard):** Die QoS-Markierung (DSCP) ausgehender Spielpakete ist nur noch eine Option (Einstellungen → „Spielpakete für Router-QoS markieren“) und standardmäßig aus. Sprachchat-Pakete werden weiter markiert.

## Wie DYNEX Spiele erkennt

- Prozessliste, Prozessnamen und Startzeiten kommen aus **einer** Systemabfrage (`NtQuerySystemInformation`), die **kein Handle** auf irgendeinen Prozess öffnet (`src/Dynex.Desktop/Native/ProcessTable.cs`). Das gilt für Spielerkennung, Vordergrundprüfung und die Zuordnung von Verbindungen im Helper.
- Nur der Programmpfad (für die Launcher-Zuordnung) braucht ein Handle mit dem schwächsten Recht `PROCESS_QUERY_LIMITED_INFORMATION` – dasselbe, das der Task-Manager nutzt. Ab 1.0.6 einmal pro Prozessstart statt alle zwei Sekunden; vorher öffnete der Helper beim Spielen mehrmals pro Sekunde Handles auf das Spiel.
- Verbindungen werden über die Windows-Verbindungstabellen (`GetExtendedTcpTable`/`UdpTable`) Programmen zugeordnet, nicht durch Einblick in Prozesse.

## Der Netzwerkfilter

Die Downloadbegrenzung nutzt den signierten Treiber **Windows Packet Filter (NDISRD)** des Herstellers NT Kernel Resources, der auch von VPN- und Firewallprogrammen eingesetzt wird. Er arbeitet unterhalb der Netzwerkkarte und berührt keine Spielprozesse. Er läuft nur, während auf einem DYNEX-PC ein Spiel im Fokus ist.

Restrisiko: Kernel-Anti-Cheats führen eigene Listen unerwünschter Treiber. Uns ist kein Eintrag für NDISRD bekannt, aber das lässt sich nicht für alle Spiele und künftige Versionen zusichern. Wer ganz sichergehen will, schaltet unter Einstellungen „Beim Spielen priorisieren und große Downloads begrenzen“ aus – dann lädt DYNEX keinen Treiber und arbeitet nur mit Spielerkennung, Anzeige und Koordination.

## Automatische Prüfung

`scripts/anticheat-guard.ps1` läuft in jeder CI und vor jedem Release. Es schlägt fehl, sobald der Quellcode Speicherzugriffe, Injektion, Eingabe-Hooks, Fensteranzeige-Tricks, ein Topmost-Overlay, `MainModule` außerhalb der Setup-Prüfung eigener DYNEX-Prozesse oder `OpenProcess` mit mehr als `QueryLimitedInformation` verwendet.

## Praxisprüfung (offen)

Mit echten Konten je 30 Minuten spielen, DYNEX aktiv, Download auf dem zweiten PC: Valorant (Vanguard), Fortnite oder Apex (EAC), Rainbow Six Siege (BattlEye), Counter-Strike 2 (VAC) und optional FACEIT. Erwartung: kein Start-Block, keine Warnung, kein Kick. Ergebnis in `docs/TESTING.md` festhalten.
