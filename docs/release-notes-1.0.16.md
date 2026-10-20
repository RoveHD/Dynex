# DYNEX 1.0.16

DYNEX arbeitet jetzt mit reiner Paketpriorisierung. Geräteübersicht, aktuelle Datenraten, Verbrauchsverlauf, Ping-Verlauf, Spielerkennung und Verbindungsdiagnose bleiben erhalten.

- **Alle Bandbreitenlimits entfernt:** keine automatischen/adaptiven Budgets, keine Download-/Upload- oder Programmregeln, keine Limit-Zeitpläne und keine Downloadpausen. Alte Einstellungen, Sicherungen und Netzwerkprofile können keine Begrenzung mehr aktivieren. Auch der Helfer verwirft alte Limitbefehle; seine Pacing-Warteschlangen sind entfernt.
- **Spielpakete tatsächlich markieren:** geeignete ausgehende Spielpakete erhalten DSCP CS4, Sprachpakete EF. ECN, Transportheader und Nutzdaten bleiben erhalten; IPv4-Headerprüfsummen werden angepasst. Ein passend konfigurierter Router muss die Markierungen auswerten. DYNEX ändert keine Routerkonfiguration und garantiert keinen bestimmten Ping.
- **Klare Anzeige:** Priorisierungsstatus und Paketzähler ersetzen Downloadbudget und Limitspalten. Eine manuelle Geschwindigkeitsmessung bleibt als Diagnose verfügbar; beim Start gibt es keinen automatischen Speedtest mehr.
- **Android ohne Drosselung:** WLAN-Begleit-App und Hintergrunddienst bleiben erhalten. DYNEX startet keine eigene VPN-Verbindung mehr. Ein bereits in AdGuard eingerichteter lokaler Proxy bleibt unbegrenzt durchlässig; neue Installationen benötigen diesen Assistenten nicht. Die Android-App erkennt oder priorisiert keine Handyspiele. Ohne Proxy-Messpfad bleiben eigene Handy-Datenraten unbekannt.

**Alle beteiligten Geräte aktualisieren:** Ältere DYNEX-Versionen können weiterhin ihre eigenen gespeicherten Limits anwenden. Kopplungen, Geräte, Spielkonfiguration und Verbrauchshistorie bleiben erhalten.

Die Android-APK verwendet denselben Signaturschlüssel wie die vorherige veröffentlichte Version. SHA-256-Dateien gehören zu den Downloads. Der Signaturstatus des Windows-Setups steht zusätzlich im Release.

Automatisierte Paket-, Runtime-, Migrations- und Oberflächenprüfungen ersetzen keinen Belastungstest mit zwei physischen PCs, Handy und Router. Es wurde kein automatisches Update auf deinen Geräten installiert.
