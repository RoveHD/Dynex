# DYNEX 0.3.1 installieren und entfernen

Das Release-Setup `artifacts/DYNEX-Setup-0.3.1.exe` installiert DYNEX pro Windows-Benutzer unter `%LOCALAPPDATA%\Programs\DYNEX`. Es enthält die Desktop-App und den erforderlichen `Dynex.Shaper.exe`-Helper. Administratorrechte und eine separate .NET-Laufzeit sind für die App-Installation nicht nötig; Programme und Setup sind nicht codesigniert.

## Start im privaten LAN

Starte DYNEX auf den PCs im selben privaten Windows-Netzwerk. AutoLAN, Windows-Autostart und Spiele-/Sprachchat-Priorisierung sind standardmäßig eingeschaltet; für den ersten Kontakt ist kein Gruppencode nötig. Eine neue Peer-Identität im privaten LAN wird beim ersten Kontakt automatisch vertraut. Optional kann weiterhin eine private gemeinsame Gruppe genutzt werden.

Der Speedtest startet automatisch genau einmal. Auch nach Fehler oder Abbruch gibt es beim nächsten Programmstart keinen automatischen Wiederholungsversuch. Danach lässt er sich über den Speedtest-Knopf manuell starten. Automatische GitHub-Updates lassen sich separat in den Einstellungen anhaken; [Details zum Ablauf](updates.md).

Der lokale Windows-PC-Name wird automatisch verwendet. Die Anschlusskapazität wird einmal gemessen und gespeichert. Ab 0.3.1 stimmen die PCs ihre unterschiedlichen Messwerte automatisch ab und teilen den kleinsten daraus errechneten Downloadtopf. Die Reserve je Gaming-PC beträgt automatisch `min(10 Mbit/s, 5 % der Kapazität)`; das Standard-Hintergrundbudget beträgt 65 %. Installiere die aktuelle Version auf jedem beteiligten PC und schalte jeweils die Downloadbegrenzung ein. Ein bereits eingerichteter Netzwerkfilter muss beim App-Update nicht erneut installiert werden.

## Netzwerkfilter einmalig einrichten

DYNEX enthält selbst weder die MSI noch NDISRD-Treiberdateien, ndisapi.dll oder WinDivert-Dateien. Wähle in DYNEX **Einrichten**. Die App lädt das offizielle Windows Packet Filter x64 MSI in das Benutzerprofil, prüft Dateigröße und festen SHA-256 und lässt Windows die Authenticode-Signatur prüfen. Eine nicht passende Hash- oder Signaturprüfung bricht ab.

Danach startet DYNEX den offiziellen MSI-Installer interaktiv mit UAC. Lies dessen Bestätigung und bestätige nur, wenn du die Installation des NDISRD-Netzwerkfilters möchtest. Das ist eine systemweite Änderung: Windows registriert einen NDIS-Filter an Netzwerkadaptern; während der Einrichtung kann die Verbindung kurz unterbrochen werden. DYNEX erzwingt keinen Neustart; Windows kann einen Neustart als erforderlich melden.

Die MSI-Installation ist unabhängig vom per-user DYNEX-Setup. Sie ist kein stiller Bestandteil von Update oder App-Start. Die Filteraktivierung findet erst statt, wenn der Benutzer danach **Downloadbegrenzung einschalten** einschaltet. Fehlt der Helper, verweigert Windows den Zugriff oder kann der Adapterfilter nicht sicher geöffnet werden, bleibt das Limit inaktiv und DYNEX zeigt einen Fehler.

## Wirkungsbereich

DYNEX berücksichtigt große Netzwerkflüsse erst nach mindestens 250 ms Laufzeit und 256 KiB übertragenen Daten. Damit werden kurze Interaktionen nicht vorschnell als Downloads gewertet. Große Downloads auf dem eigenen Gaming-PC werden ebenfalls aus seinem Anteil begrenzt. Spiele sowie Discord, Microsoft Teams, Zoom, TeamSpeak und Mumble sind ausgenommen; ebenso reine TCP-ACKs, DNS und privater LAN-Verkehr. Unbekannte Flows sowie veraltete oder nicht sicher zuordenbare Prozess-IDs werden ebenfalls ausgenommen.

Die adaptive Pingreserve ist immer aktiv und hat keinen sichtbaren Ein-/Aus-Schalter. Eine gültige Leerlauf-Baseline des Gaming-Peers kann das Hintergrundbudget am Download-PC anpassen. Das verspricht keine feste Pingzahl oder bestimmte Verbesserung. Eine Prozesserkennung bestätigt keinen Multiplayerstatus oder aktives Match.

Der Filter ist adapter- und systemweit. Andere VPN-, Sicherheits-, Virtualisierungs- oder Filtersoftware kann mit NDISRD um denselben Adapter konkurrieren. DYNEX darf einen belegten oder nicht eindeutig verfügbaren Filter nicht übernehmen; bei Konflikt bleibt die Begrenzung aus. Eine DYNEX-Deinstallation entfernt den NDISRD-Filter nicht: andere installierte Apps können davon abhängen.

## Aktualisieren und entfernen

Vor Installation, Aktualisierung oder Deinstallation sendet das Setup `DYNEX.exe --shutdown` über die benutzerspezifische Shutdown-Steuerung. DYNEX meldet Erfolg erst nach geordnetem Beenden und Freigabe des Singletons. Das Setup wartet bis zu 20 Sekunden auf den bestätigten Abschluss und bricht bei Fehler oder Timeout ab. Es beendet keine fremden Prozesse zwangsweise.

Die DYNEX-Deinstallation entfernt nur Paketdateien, DYNEX-Verknüpfungen, die DYNEX-Deinstallationsinformationen und den HKCU-Autostartwert `DYNEX`. Zusätzliche Dateien bleiben erhalten, wenn sie den Programmordner nicht leer werden lassen. Das lokale `%LOCALAPPDATA%\DYNEX`-Profil bleibt standardmäßig bestehen; das Setup fragt optional, ob es gelöscht werden soll.

Wenn du den systemweiten Netzwerkfilter später nicht mehr benötigst, öffne **Einstellungen → Apps → Installierte Apps** in Windows und deinstalliere **Windows Packet Filter x64** über den offiziellen Windows-Deinstallationsweg. Prüfe zuerst, ob andere VPN-/Netzwerkprogramme den Filter benötigen. DYNEX führt diese Entfernung bewusst nicht automatisch aus. Eine Windows-Rückfrage oder ein erforderlicher Neustart richtet sich nach dem Zustand des Treibers und der Adapter.

DYNEX ändert keine Routereinstellungen und legt keine Firewallregel still an. Für AutoLAN kann UDP 39274 im privaten lokalen Subnetz nötig sein; erlaube es bei einer Windows-Abfrage nur im privaten Netzwerk.

## Lizenz und Grenzen

Der Paketfilterhersteller nennt private, akademische und Evaluierungsnutzung als kostenlos. Das ist keine pauschale Erlaubnis für kommerzielle Nutzung oder Redistribution; dafür ist eine gesonderte Herstellerlizenz zu prüfen. Das MSI wird aus diesem Grund nicht in den DYNEX-Installer aufgenommen.

Siehe [Shaping und Grenzen](shaping.md) sowie [Backend-Details](ndis-backend.md), falls diese Datei im Paket vorliegt.
