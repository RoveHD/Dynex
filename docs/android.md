# Android als Begleit-App ohne Limits

Die Android-App verbindet sich im selben WLAN mit DYNEX-PCs und zeigt deren Gamingstatus sowie verfügbare Telemetrie. Sie läuft als Vordergrunddienst weiter, wenn die Oberfläche geschlossen wird. Bei Mobilfunk und ohne passendes WLAN wartet die LAN-Kommunikation energiesparend auf einen Netzwerkwechsel.

## Keine Drosselung und keine mobile Spielerkennung

Automatische Budgets, manuelle Handy-Limits und Gewichtungen sind entfernt. Alte gespeicherte Limits werden neutralisiert. Der native VPN-Tunnel wird nicht mehr zum Begrenzen gestartet; eine frühere Sitzung wird beim Start des neuen Dienstes beendet. Die App erkennt keine Handyspiele und meldet keine erfundene lokale Spielpriorisierung.

Auch die verbliebenen Proxy-/Tunnel-Kompatibilitätsschnittstellen wenden keine Raten mehr an. Direkte Aufrufe mit alten Raten können keine Weiterleitung drosseln.

## Bereits eingerichtetes AdGuard

Ein in AdGuard gespeicherter DYNEX-SOCKS5-Proxy bleibt im bisherigen AdGuard-Modus als unbegrenzte Durchleitung verfügbar. Dadurch lässt ein Update die bestehende Weiterleitung nicht ins Leere laufen. Das gilt auch für Mobilfunk. Die App startet keinen neuen AdGuard-Einrichtungsassistenten für diese entfernte Funktion.

Wenn AdGuard noch Daten über den Proxy schickt, müssen DYNEX und der bestehende Dienst weiterhin laufen, bis der Nutzer die alte Proxy-Konfiguration in AdGuard entfernt. DYNEX ändert diese fremde Konfiguration beim Update nicht stillschweigend.

## Messwerte

Ein bestehender Proxy misst nur den tatsächlich durch ihn weitergeleiteten Verkehr. Ohne diesen Pfad bleibt der Handyverbrauch unbekannt; die App zeigt keine angenommenen Nullwerte und behauptet keine vollständige systemweite Verkehrsmessung.

Die portable Policy-, Routing- und Proxytests prüfen den Betrieb ohne Caps sowie vorhandene Sicherheits- und Routinggrenzen. Eine erfolgreiche APK-Kompilierung ersetzt keinen Test auf einem physischen Handy.
