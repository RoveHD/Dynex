# DYNEX 1.0.18

- Öffentliche spezifische Medien-CDN-Hostnamen bleiben als Hinweis nutzbar, wenn der ClientHello eine ECH-/GREASE-Extension enthält. Der Inhalt der Extension bleibt undurchsichtig; allgemeine Cover-Hostnamen und fehlende sichtbare Medien-SNI erhalten keine zusätzliche Priorität.
- Die Änderung entschlüsselt oder errät keinen Inner-SNI und verändert keine Browser-/TLS-Sicherheitseinstellung. Sie behebt eine pauschale Ablehnung, die auch GREASE traf.
- IPv4/IPv6, TCP und QUIC sowie Browser-/ELFIX-Verbindungen werden mit sichtbarer Medien-SNI und GREASE geprüft; allgemeine beziehungsweise fehlende Cover-SNI bleibt unmarkiert.

Die Ursache einer bereits laufenden Brave-Stream-Verbindung ohne AF41 wurde mit dem vorhandenen Mitschnitt nicht bestimmt: Er enthielt keine sichtbare Medien-CDN-Verbindung und teilweise unvollständige Handshakes. Bereits bestehende Verbindungen ohne beobachteten Handshake können weiterhin nicht nachträglich sicher als Video erkannt werden. Weitere Grenzen: [QoS](qos.md).
