# DYNEX 1.0.27

Behebt „Update konnte nicht gestartet werden: Zugriff verweigert“ beim Beenden des geschützten Netzwerkhelfers vor einem Update.

Der Desktop beobachtet den Helfer mit den dafür vorgesehenen eingeschränkten Windows-Rechten. Die Freigabebestätigung und das tatsächliche Prozessende bleiben Voraussetzung für die Übergabe an den Installer. Die Rechte des SYSTEM-Helfers werden nicht erweitert.

Windows und Android erhalten Version 1.0.27. Android behält den bisherigen Update-Schlüssel; die funktionale Änderung betrifft Windows.

Falls eine ältere Windows-Version bereits vor dem Start des Installers mit „Zugriff verweigert“ abbricht, das neue Setup einmal direkt aus diesem Release starten. Danach verwendet DYNEX den korrigierten Updatepfad. Einstellungen und Geräteidentität bleiben erhalten.
