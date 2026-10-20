# DYNEX 1.0.19

ELFIX-Videoverbindungen können jetzt auch bei unbekannten verschlüsselten Hostern automatisch erkannt werden. Die dafür aktualisierte ELFIX-Windows-App meldet echte Medienantworten über eine lokale Benutzer-Pipe; eine manuelle Einrichtung oder Kopplung ist nicht nötig. DYNEX prüft den tatsächlichen ELFIX-Prozess, Benutzer, Programmdatei, Startzeit und die frische Socketzuordnung. Übertragen werden ausschließlich Prozess-IDs und Server-IP/Port, keine Video-URLs, Titel, Cookies oder Inhalte.

- **Spiele und Sprache zuerst:** Spiele erhalten weiterhin CS4, Sprache EF, nachgewiesene Videos darunter AF41. Der Router muss diese Markierungen auswerten. Eine bestimmte Latenz wird nicht zugesichert.
- **Keine Bandbreitenlimits:** Downloads und Uploads bleiben uneingeschränkt; es gibt weiterhin keine gemeinsamen Budgets oder adaptiven Kürzungen.
- **Auch ohne laufendes Spiel aktiv:** Die eingeschaltete allgemeine Priorisierung bleibt für Sprache und erkannte Videos aktiv. Abschalten, manuelle Diagnose und Beenden geben die Sitzung frei.
- **Passive Videoerkennung:** Sichtbare Medien-CDN-SNI für YouTube/Netflix, begrenzte TCP-/QUIC-v1/v2-Handshake-Erkennung und sichtbare HTTP-Medientypen ergänzen die ELFIX-Meldung. ECH-/GREASE-Metadaten werden nicht entschlüsselt; nur tatsächlich sichtbare spezifische Mediennamen zählen.
- **Nachvollziehbare Anzeige:** Ein eigener Video-Paketzähler wird vom Helfer bis in die Übersicht übertragen. Ein Zähler bestätigt die lokale Markierung, nicht die Behandlung durch den Router.
- **Begrenzte lokale Meldungen:** Nachrichten haben feste Größen- und Häufigkeitsgrenzen. Verbindungsabbruch widerruft die Freigabe; ohne rechtzeitige Erneuerung läuft sie nach 15 Sekunden aus. Andere Programme oder Serverports übernehmen die Freigabe nicht.

Für die automatische ELFIX-Erkennung **DYNEX und ELFIX aktualisieren**. Die Android-App bleibt eine Begleit-App ohne eigene Video- oder Spielpaketpriorisierung; bestehende AdGuard-Weiterleitung bleibt unbegrenzt.

ELFIX-Anfragen an exakt denselben Server-Endpunkt können dieselbe verschlüsselte HTTP/2-/HTTP/3-Verbindung teilen; einzelne Anfragen können darin nicht gesondert markiert werden. Ohne Anwendungsmeldung oder geeigneten passiven Nachweis bleibt unbekannter Verkehr unmarkiert. Details: [ELFIX-Integration](elfix-integration.md), [QoS](qos.md).

Windows-Setup und Programme bleiben mangels eingerichtetem Code-Signing-Zertifikat unsigniert. SHA-256-Prüfsummen liegen bei. Die Android-APK verwendet den bestehenden Update-Schlüssel. Automatisierte Tests mit echten lokalen HTTPS-Anfragen und Windows-Prozesszuordnung ersetzen keinen Lasttest am physischen Router oder mit erhöhtem Kernel-Helfer.
