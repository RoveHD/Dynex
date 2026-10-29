# DYNEX 1.0.23

Behebt den Installationsfehler 22 beim Update von älteren Versionen auf den geschützten Priorisierungsdienst.

Der bisherige Installer kopierte neue Helferdateien über den vorhandenen Ordner. Dabei konnten alte .NET-Runtime-DLLs zurückbleiben. Die seit 1.0.22 strenge Paketprüfung erkannte diese zusätzlichen Dateien und stellte deshalb die vorherige Version wieder her. Das betraf sowohl automatische Updates als auch manuelle Installationen.

- Das Setup verschiebt den bisherigen Helferordner vor dem Kopieren vollständig in die Rücksicherung. Die neue Version erhält einen sauberen Ordner mit genau ihrem geprüften Paketinhalt.
- Scheitert das Update, stellt das Setup den vollständigen alten Helferordner einschließlich Unterordnern wieder her. Eine noch vorhandene unvollständige Rücksicherung wird nicht überschrieben.
- Die Installationsprüfung deckt jetzt Upgrades mit alten Runtime-Dateien und die vollständige Wiederherstellung bei Fehlern ab. Die Integritätsprüfung bleibt unverändert streng.

Das Update auf 1.0.23 kann auch von einer nach dem fehlgeschlagenen 1.0.22-Update wiederhergestellten Version automatisch geladen werden. Einstellungen, Geräte, Kopplungen und Messhistorien bleiben erhalten. Für die erstmalige Einrichtung beziehungsweise Aktualisierung des Windows-Dienstes ist weiterhin eine Windows-Bestätigung nötig; normale Starts benötigen danach keine wiederholte Administratorabfrage.

Android erhält die Versionsnummer 1.0.23 mit dem bestehenden Update-Schlüssel. Der Installationsfix betrifft Windows.
