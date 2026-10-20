# Paketverarbeitung ohne Bandbreitenlimits

Der historische Projektname `Dynex.Shaper` bleibt bestehen. Der aktive Betrieb ist reine Priorisierung: `PacketPump` empfängt, beobachtet, klassifiziert und leitet jedes Paket unmittelbar weiter. Es gibt keine Pacing-Aufgaben, keine getrennten App-/Upload-/Downloadqueues und keine bandbreitenbedingten Drops.

Der Desktop setzt globale Download-/Uploadraten stets auf null und Programmregeln auf leer. Der IPC-Parser validiert die begrenzte alte Nachrichtensyntax weiter, entfernt aber darin enthaltene Raten und Regeln. Die verbliebenen veralteten Methoden `UpdateLimit`, `UpdateUploadLimit` und `UpdateApplicationRules` sind wirkungslos. Ein alter Cap-Befehl allein startet keine Capture-Sitzung.

Ausgehende interaktive Spiel-/Sprachpakete und nachgewiesene Videoverbindungen erhalten DSCP-Markierungen. Transportheader, Nutzdaten und ECN bleiben erhalten; eingehende Pakete werden nicht umgeschrieben. Siehe [QoS](qos.md).

Bei Beenden, fehlenden Heartbeats oder Netzwerkwechsel wird der native Filter sicher freigegeben. Ein fehlender oder fehlerhafter Treiber wird sichtbar gemeldet und niemals als aktive Priorisierung ausgegeben. Eine nicht bestätigte Freigabe darf keine automatische Wiederöffnung erlauben.

Die gemeinsame Bandbreitenregelung liegt beim Router. Frühere Budget- und Limitberechnungen sind keine aktiven Produktfunktionen mehr.
