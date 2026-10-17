# Adaptive Downloadreserve anhand der Gaming-PCs

`LimitPlanner` verwendet aktuelle Ping-Abweichungen der Gaming-PCs gegenüber deren jeweiligen Idle-Baselines, um das gemeinsame Hintergrundbudget auf jedem teilnehmenden PC anzupassen. Ein Beispiel: Ein Gaming-PC meldet unbelastet 20 ms und unter Last 90 ms; der Download-PC misst weiterhin 20 ms. Wenn dessen lokale Downloadlast mindestens 75 % seines aktuellen Anteils erreicht und die erhöhten Gaming-Messungen anhalten, sinkt sein Budgetanteil. Ein lokal gamingfähiger Rechner kann zusätzlich seine eigene schlechte Latenz als Congestion-Evidenz verwenden, auch wenn entfernte Gaming-PCs gesunde Werte melden.

Das ist eine adaptive Reserve, keine Zusage von 30 ms oder eines bestimmten Spielpings. Ein externes ICMP-Messziel ist ein Näherungswert für die gemeinsame Verbindung. Serverauslastung, WLAN, Routing, Uploads und die bei diesem PC bereits eingetroffenen UDP-Daten können weiterhin Latenz verursachen. Die Policy diagnostiziert damit auch nicht sicher Bufferbloat.

## Vertrauens- und Messgrenze

Seit 1.0.14 wird kein gemeinsames Maximum mehr manuell eingetragen. DYNEX leitet den Pool aus den gültigen gemessenen Anschlusswerten, dem jeweiligen Profilanteil und der Reserve je Spieler ab. Frische Bulk-Downloadmessungen des PC-Filters und Handy-Proxys verteilen ihn gewichtet auf aktive Downloader; bei unbekannter Aktivität werden weiterhin alle Geräte berücksichtigt. Ein ruhendes Gerät hat eine begrenzte Startzuteilung. Neue oder endende Downloads ändern die Aufteilung nach dem nächsten Statusaustausch, daher kann die Summe während dieses Übergangs kurz über dem Pool liegen. Uploadanteile verwenden unverändert alle Teilnehmer; Details stehen in [protocol.md](protocol.md#shared-download-budget-proposals).

Der Android-Verbraucher verwendet denselben zustandsbehafteten Regler mit echten lokalen Lastmessungen und frischen, sequenzierten PC-Latenzmeldungen. Er misst keinen eigenen Ersatzping. Manuelle Gerätelimits werden vor der Lastschwelle und dem adaptiven Faktor berücksichtigt: Bei automatischem Anteil 60 und manuellem Maximum 25 senkt ein Schritt unmittelbar auf 21,25 Mbit/s. Eine fehlende PC-Leerlaufreferenz wird im Handy-Status angezeigt.

Ab 0.3.1 enthalten frische Statusmeldungen zusätzlich einen `BudgetProposal` aus Anschlussgeschwindigkeit, Profilanteil und Reserve je Spieler. Alle aktuellen Vorschläge werden für dieselbe erkannte Gameranzahl ausgewertet; der kleinste verbleibende Topf ist das gemeinsame Budget. Ab 1.0.14 wird dieses Budget auf aktive Downloader entsprechend den bestehenden Prioritätsgewichten verteilt; bei unbekannter Aktivität zählen vorsichtig alle Teilnehmer. Adaptive Faktoren und lokale manuelle Gerätelimits können einzelne Anteile zusätzlich reduzieren, erhöhen aber keinen Anteil über seine gemeinsame Zuteilung. Ein PC mit 1.0.13 kann neue Vorschläge ohne Maximum empfangen, aber einen dort gespeicherten niedrigeren Wert weiterhin anwenden. Für durchgehend automatische gemeinsame Planung müssen alle PCs 1.0.14 nutzen. Die Berechnung setzt gegenseitig erreichbare, aktuelle Peers mit demselben Teilnehmerstand voraus; es gibt keinen zentralen Routerregler oder atomare Reservierung über Netzwerktrennungen hinweg.

Die App zeigt den gemeinsamen Topf und die Anzahl bestätigter aktiver Begrenzungen. Alte Peers ohne Budgetvorschlag werden weiter erkannt und mitgezählt, die gemeinsame Abstimmung aber als unvollständig angezeigt. Ein 1.0.13-PC kann Vorschläge ohne Maximum empfangen; ein dort gespeicherter gemeinsamer Höchstwert kann seine Planung weiterhin absenken. Bei so wenig Kapazität, dass nicht einmal 0,1 Mbit/s je PC möglich sind, kann die minimale darstellbare Begrenzung keine gemeinsame Obergrenze einhalten; auch dieser Fall gilt als unvollständig.

Wird DYNEX erstmals während eines laufenden Spiels gestartet, gilt der statische gemeinsame Topf sofort. Ohne vorher gelernte unbelastete Referenz kann die adaptive Regelung jedoch noch keine zusätzliche Pingverschlechterung beurteilen. Eine belastete Messung wird nicht als vermeintlicher Leerlaufwert ausgegeben.

Die Eingabe `PeerAdvertisement` muss aus dem authentifizierten LAN-Dienst stammen. `LimitPlanner` ist eine reine Policy und kann die Authentifizierung eines manuell erzeugten Objekts nicht nachprüfen. Ein Peer zählt nur innerhalb der konfigurierten TTL und mit gültiger Geräte-ID im übernommenen Snapshot; doppelte Geräte werden auf den neuesten Status reduziert.

Neue optionale Felder:

| Feld | Bedeutung |
|---|---|
| `IdleBaselineMs` | Im unbelasteten Zustand gelernter RTT; 0 ms ist zulässig |
| `LatencyTargetId` | Stabiles externes Messziel einschließlich lokaler Route; maximal 256 Zeichen ohne Steuerzeichen |
| `LatencySampleSequence` | Eigene Nummer einer veröffentlichten Messung; unveränderte Heartbeats erhalten dieselbe Nummer |
| `IdleBaselineAtUtc` | Tatsächlicher Zeitpunkt der lokalen Baseline-Ermittlung |

Baselines müssen endliche Werte zwischen 0 und 10000 ms haben, höchstens 30 Minuten alt und nicht mehr als fünf Sekunden in der Zukunft liegen. Ohne Altersbeleg oder Messziel gelten sie nicht als zuverlässiges Peerfeedback. `LimitDecision.IdleBaselineAtUtc` gibt den tatsächlichen lokalen Lernzeitpunkt weiter. Der Publisher setzt die Baseline beim Ziel-/Routenwechsel zurück; der Planner verwirft außerdem bereits angesammelte Peer-Proben, sobald Ziel, Baseline oder deren Zeitpunkt wechselt.

Die Probennummer ist von der Transport-Replay-Sequenz getrennt: Wiederholte Heartbeats halten die Verbindung frisch, erzeugen aber keine neuen Latenzproben. Der Planner fordert zusätzlich einen neueren lokalen Empfangszeitpunkt. Fehlt die optionale Probennummer bei einem älteren Aufrufer, dient der Empfangszeitpunkt zur Deduplizierung. Negativwerte, NaN, ungültige Ziele und ungültige Sequenzen zählen nicht als Evidenz. Die Transport-Codecs validieren neue Metriken; das verschlüsselte Datagramm bleibt auf 2048 Bytes begrenzt, damit auch zulässige Unicode-Namen plus Ziel-/Baseline-Metadaten hineinpassen.

`PeerAdvertisement.SessionId` übernimmt die bereits authentifizierte Wire-Session. Bei einem Dienst-/Prozessneustart beginnt eine neue Session mit neuer Deduplizierung, auch wenn Baseline und Messziel erhalten bleiben und die Probennummer wieder bei 1 startet. Innerhalb derselben Session bleiben kleinere/gleiche Nummern ausgeschlossen. Das Zurückweisen alter authentifizierter Sessions und Transport-Replays bleibt Aufgabe des LAN-Diensts.

## Reduktion und Erholung

Eine frische erhöhte Messung eines Gaming-PCs kann eine Reduktion auslösen. Als erhöht gilt RTT über Baseline plus `max(8 ms, 35 % der Baseline)`. Mindestens drei unterschiedliche erhöhte Messungen müssen über wenigstens zehn Sekunden vorliegen; eine Änderung verlangt auch nach der letzten Anpassung mindestens zehn Sekunden Abstand. Ohne eigene ausreichend hohe, höchstens zehn Sekunden alte Downloadlast erfolgt keine Reduktion. Ein fehlender zweiter Gamer verhindert nicht, dass ein anderer mit belegtem erhöhtem RTT geschützt wird.

Ein Reduktionsschritt multipliziert den Faktor mit 0,85. Ab 1.0.15 wird dieser Schritt zuerst auf Wirkung geprüft: Mindestens drei neue vergleichbare RTT-Runden über wenigstens zehn Sekunden müssen bei weiterhin hoher Last eine Verbesserung um `max(2 ms, 15 % des vorherigen RTT-Überschusses)` zeigen, bevor eine weitere Kürzung erlaubt wird. Gleichbleibend hoher Ping kann das Limit daher nicht fortlaufend herunterregeln. Der Faktor kann nur bei wiederholt belegter Verbesserung bis auf 0,1 sinken; das reale Limit bleibt mindestens 0,1 Mbit/s.

Bleibt der letzte Versuch 30 Sekunden ohne nachgewiesene Wirkung, wird er in Schritten von +0,1 je zehn Sekunden zurückgenommen. Die erfolglose Episode bleibt gesperrt, auch nach langer Wartezeit. Erst drei frische gesunde RTT- oder gemessene Niedriglast-Runden über wenigstens zehn Sekunden beziehungsweise ein tatsächlicher Wechsel der Baseline, des Ziels oder der Session erlauben eine neue Probe. Lokale und entfernte Beobachtungen desselben erfolglosen Versuchs dürfen sich nicht beim erneuten Kürzen abwechseln. Fehlt 30 Sekunden lang frische Evidenz der verursachenden Quelle, erfolgt eine begrenzte Rückkehr bis Faktor 1. Diese Sicherheitsrückkehr behauptet keine gemessene Pingverbesserung.

Die konfigurierten Ausgangsbudgets, Gamerreserven und aktiven Downloader bestimmen weiterhin die statische Verteilung. Gaming-PCs erhalten ebenfalls einen Anteil: Nur Spielpakete sind auf Paketebene ausgenommen; große Downloads auf demselben PC werden aus dessen Anteil begrenzt. Adaptive Kürzungen werden nie als neue Anschlusskapazität gespeichert oder weitergesendet.

Erholung ist strenger: Alle aktuell aktiven Gaming-Peers müssen gültige neue Messdaten und RTT unter Baseline plus `max(4 ms, 15 % der Baseline)` liefern. Fünf vollständig neue Runden über mindestens zwanzig Sekunden erlauben einen Schritt von +0,05 bis maximal Faktor 1. Wenn der lokale Rechner selbst spielt, muss zusätzlich seine eigene frische, gültige Leerlauf-Baseline vorliegen und sein lokaler Probe-Ping gesund sein. Ein gesunder Peer kann keine fehlende oder unverändert alte Messung eines anderen ersetzen. Wiederholtes Polling derselben Messungen zählt nicht erneut.

Der lokale ICMP-/Last-Regler bleibt als Fallback verfügbar, solange kein gültiger Gaming-Peer-Baselinewert vorliegt. Ein lokaler Spieler ist ebenfalls eine maßgebliche Latenzquelle: Schlechte lokale Gaming-Latenz kann die lokale Reserve auch dann reduzieren, wenn entfernte Gamer gesund sind. Die normale Erholung nach bestätigter Verbesserung benötigt weiterhin die gesunden Gaming-Messwerte; fehlende Evidenz nutzt stattdessen den beschriebenen begrenzten Sicherheitsrückweg. Lokales Gaming setzt den Faktor nicht zurück und hebt das lokale Budget nicht auf. Erst wenn Gaming auf allen teilnehmenden DYNEX-PCs endet, wird die automatische Begrenzung freigegeben; ein separat konfiguriertes manuelles Limit bleibt bestehen.

## Prüfung

```powershell
dotnet run --project tests/Dynex.Adaptive.Tests/Dynex.Adaptive.Tests.csproj -c Release
dotnet run --project tests/Dynex.Core.Tests/Dynex.Core.Tests.csproj -c Release
```

Die isolierten Adaptive- und Coreprüfungen decken unter anderem dauerhaft 90 ms ohne wiederholte Kürzungen, nachweislich wirksame Folgeschritte, Wiederholung derselben Probe, fehlende oder abgelaufene Baselines, Ziel-/Sessionwechsel, mehrere Gamer, manuelle Gerätelimits und die Erholung ab. Es werden keine Routereinstellungen, Treiber oder tatsächlichen Netzwerkraten verändert. Ein realer Spiel-/WAN-Latenztest wird dadurch nicht ersetzt.
