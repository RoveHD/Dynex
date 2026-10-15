# DYNEX und Anti-Cheat

DYNEX ist so gebaut, dass Spiele und ihre Anti-Cheat-Systeme (z. B. Riot Vanguard, Easy Anti-Cheat, BattlEye, VAC, FACEIT, Ricochet) nichts sehen, was wie ein Cheat aussieht. Eine Garantie kann niemand außer dem jeweiligen Hersteller geben; die folgenden Regeln sind aber technisch durchgesetzt und werden bei jedem Push geprüft.

## Was DYNEX niemals tut

- **Kein Zugriff auf Spielspeicher:** kein Lesen oder Schreiben von Prozessspeicher, keine Modulliste (`ReadProcessMemory`, `WriteProcessMemory`, `PROCESS_VM_*`, `.Modules`).
- **Keine Code-Injektion:** keine Remote-Threads, keine DLLs im Spielprozess, keine APCs.
- **Keine Eingabe- oder Fenster-Hooks:** kein `SetWindowsHookEx`, `SetWinEventHook`, `SendInput`; DYNEX liest nur, welches Fenster im Vordergrund ist.
- **Kein Overlay:** DYNEX zeichnet nicht über Spiele. Status gibt es im Infobereich und in Windows-Benachrichtigungen.
- **Keine verzögerten oder verworfenen Spielpakete:** Pakete erkannter Spiele umgehen Download- und Upload-Warteschlange immer (`PacketPump`, `BulkDownloadPolicy.ShouldLimitUpload`). Ein „Lag-Switch“-Muster entsteht dadurch nicht. Begrenzt werden nur große Downloads anderer Programme.
- **Spielpakete werden nie verändert, nur priorisiert:** Sie überspringen jede Warteschlange und verlassen den PC Byte für Byte so, wie das Spiel sie gesendet hat. Eine Option zum Markieren oder Umschreiben von Spielpaketen gibt es ab 1.0.6 nicht mehr (Tests prüfen das für IPv4/IPv6 und im Paketpfad). Nur Sprachchat-Pakete bekommen eine QoS-Markierung.

## Wie DYNEX Spiele erkennt

- Prozessliste, Prozessnamen und Startzeiten kommen aus **einer** Systemabfrage (`NtQuerySystemInformation`), die **kein Handle** auf irgendeinen Prozess öffnet (`src/Dynex.Desktop/Native/ProcessTable.cs`). Das gilt für Spielerkennung, Vordergrundprüfung und die Zuordnung von Verbindungen im Helper.
- Nur der Programmpfad (für die Launcher-Zuordnung) braucht ein Handle mit dem schwächsten Recht `PROCESS_QUERY_LIMITED_INFORMATION` – dasselbe, das der Task-Manager nutzt. Ab 1.0.6 einmal pro Prozessstart statt alle zwei Sekunden; vorher öffnete der Helper beim Spielen mehrmals pro Sekunde Handles auf das Spiel.
- Verbindungen werden über die Windows-Verbindungstabellen (`GetExtendedTcpTable`/`UdpTable`) Programmen zugeordnet, nicht durch Einblick in Prozesse.

## Zählen

- „Wer lädt gerade?“ zählt nur Bytes pro Programm, während die Pakete ohnehin durch die Priorisierung laufen. Kein Paket wird dafür angehalten oder verändert.

## Der Netzwerkfilter

Die Downloadbegrenzung nutzt den signierten Treiber **Windows Packet Filter (NDISRD)** des Herstellers NT Kernel Resources, der auch von VPN- und Firewallprogrammen eingesetzt wird. Er arbeitet unterhalb der Netzwerkkarte und berührt keine Spielprozesse. Er läuft nur, während auf einem DYNEX-PC ein Spiel im Fokus ist.

Restrisiko: Kernel-Anti-Cheats führen eigene Listen unerwünschter Treiber. Uns ist kein Eintrag für NDISRD bekannt, aber das lässt sich nicht für alle Spiele und künftige Versionen zusichern. Wer ganz sichergehen will, schaltet unter Einstellungen „Beim Spielen priorisieren und große Downloads begrenzen“ aus – dann lädt DYNEX keinen Treiber und arbeitet nur mit Spielerkennung, Anzeige und Koordination.

## Automatische Prüfung

`scripts/anticheat-guard.ps1` läuft in jeder CI und vor jedem Release. Es schlägt fehl, sobald der Quellcode Speicherzugriffe, Injektion, Eingabe-Hooks, Fensteranzeige-Tricks, ein Topmost-Overlay, `MainModule` außerhalb der Setup-Prüfung eigener DYNEX-Prozesse oder `OpenProcess` mit mehr als `QueryLimitedInformation` verwendet.

## Kein Praxistest mit echten Konten

DYNEX wird bewusst nicht durch Spielen mit eigenen Konten „gegen Anti-Cheat getestet“: Ein Bann wäre das Risiko nicht wert, und ein ausbleibender Bann bewiese ohnehin nichts für andere Spiele oder spätere Versionen. Stattdessen verlässt sich DYNEX auf die oben genannten Regeln, die automatisch geprüft werden.

Wer jedes Restrisiko durch den Treiber vermeiden will, nutzt DYNEX ohne Netzwerkfilter („Beim Spielen priorisieren und große Downloads begrenzen“ aus bzw. Filter nicht einrichten). Der Treiber bleibt installiert und an die Netzwerkkarte gebunden, auch wenn DYNEX gerade nichts filtert; vollständig weg ist er nur nach der Deinstallation über DYNEX.
