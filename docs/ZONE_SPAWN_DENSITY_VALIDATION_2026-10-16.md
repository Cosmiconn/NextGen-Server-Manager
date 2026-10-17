# Zone00: Pool-Limit und Spawn-Verteilung

Stand: 16.10.2026, abgeschlossen nach Bereinigung um 20:41 MESZ. Die strengen 1600er-Rampen auf **RouN und TSStart mit verteilten Positionen sind bestanden**. Das belegt die Aufnahme über das Stock-Limit von 1500 hinaus und eine funktionierende Konfiguration für diese getestete Last. Es ist keine allgemeine Freigabe für 2000 gleichzeitig spielende Clients und kein universeller Fix des ShadowStep-Pfads.

## Vergleich mit dem vorherigen Lauf

| Bedingung | TSStart, bisherige Positionen (15.10.) | RouN, verteilt (16.10.) | TSStart, verteilt (16.10.) |
| --- | --- | --- | --- |
| Zone.exe / Listener | 2000/12000/512, Listener 2000 | identisch | identisch |
| Starttakt / Nachaufnahme | 2 Sekunden / ausgeschaltet | identisch | identisch |
| Testidentitäten | vorhandene 1600 Charaktere | dieselben 1600 | dieselben 1600 |
| Aufnahme | bis 1200 bestanden; Abbruch bei 1299 ready / 1 Fehler | alle 26 Stufen bis 1600 | alle 26 Stufen bis 1600 |
| Finale Stabilität | nicht erreicht | 5 Minuten, 60 exakte Samples | 5 Minuten, 60 exakte Samples |
| Disconnect 1669 | 15 Ereignisse, 8 Identitäten | keine neuen Ereignisse | keine neuen Ereignisse |
| Laufzeitlogs | BLOCKED nach korrigierter Auswertung | CLEAN, keine Evidenzlücken | CLEAN, keine Evidenzlücken |
| Startlogs | siehe vorherigen Bericht | CLEAN, keine Evidenzlücken | CLEAN, keine Evidenzlücken |

Der RouN-Lauf dauerte einschließlich Start und Cleanup **11:04–12:09 MESZ**; die Rampe lief **11:06–12:09 MESZ**. Alle 1600 Charaktere hatten danach genau einen zusätzlichen gespeicherten Login, unveränderte geplante Koordinaten auf RouN und positive HP. Alle 1600 Positionen waren verschieden. Reguläre Handoff-Retries bleiben Teil des bestehenden Protokolls; es wurden keine zusätzlichen Retry-Freigaben eingeführt.

Die frühere Fehleraufnahme lag bei 1299/2000 belegten Player-Slots. Zusammen mit dem neuen vollständigen Lauf spricht dies gegen ein verborgenes festes 1200-/1500-Player-Limit als Erklärung des TSStart-Fehlers. **2000 ist das unabhängig gelesene Pool-Maximum, 1600 die hier tatsächlich getestete gleichzeitige Belegung.** Kampf-, Bewegungs- und Langzeitlast sind dadurch nicht zertifiziert.

## Datengrundlage und Sicherungen

- Vor Änderungen waren alle 1600 Testcharaktere auf TSStart. Die größten exakten Positionshäufungen umfassten 128, 102 und 95 Charaktere. Alle waren Level 1 und hatten positive HP.
- Vor dem ersten Update: frische SQL-Server-Sicherung der Charakterdatenbank mit `COPY_ONLY, CHECKSUM`; `RESTORE VERIFYONLY WITH CHECKSUM` erfolgreich.
- Zusätzlich exakter Positionssnapshot einschließlich Map, X/Y, Richtung, Charakter-/Accountbindung. Updates erfolgten ausschließlich für die 1600 Manifestidentitäten in einer Transaktion mit Identitäts- und Zeilenzahlprüfung, bei gestoppten Fiesta-Diensten und leerer Charakter-Ladetabelle.
- RouN ist laut bestehenden Field-/MapLoad-Daten Teil von Zone00. Routing, Mob-/NPC-Daten, Patch, Listener und Login-Protokoll wurden für den Vergleich nicht geändert.
- Kandidaten lagen in vollständig freien SHBD-Bytes, zusammenhängend mit dem bestehenden Karten-Spawn, mit Nachbarschaftsabstand zu blockierten Bytes und Abstand zu statischen NPCs/Gates. Das 50er-Raster ergab 1600 verschiedene Punkte. Dies war zunächst nur eine konservative Dateiprüfung; erst erfolgreiche Aufnahme und anschließende serverseitige Speicherung bestätigten die tatsächlich verwendeten Positionen.
- Ein vorgeschalteter 32-Spieler-Pilot bestand drei Stufen und zwei Minuten Stabilität ohne 1669. Alle 32 kamen beim ersten Zone-Handoff an und blieben exakt auf den vorgesehenen Punkten; die übrigen 1568 Positionen blieben unverändert.

Die Startlog-Prüfung dieses Piloten meldete REVIEW wegen einer beim Neustart gekürzten `Message.txt` und eines Checksum-Hinweises. Der Pilot-Runtimeaudit war CLEAN. Diese Befunde werden nicht als sauberer gesamter Pilot-Start umgedeutet; der danach ausgeführte vollständige RouN-Lauf hat einen eigenen CLEAN-Start- und Runtimeaudit.

## Sichere Betriebsgrenze und offene Ursache

Das unveränderte Patchprofil **Player 2000 / Mob 12000 / NPC 512** ist in beiden kontrollierten Szenarien mit 1600 gleichzeitigen Sitzungen funktionsfähig. Ein Pool-Maximum allein ist keine Lastzusage. Alle Testcharaktere bleiben abschließend auf den live geprüften verteilten TSStart-Positionen; ursprüngliche Positionen und die geprüfte RouN-Verteilung sind separat gesichert. Alle Fiesta-Dienste sind nach der unten beschriebenen Bereinigung gestoppt.

Der erfolgreiche zweite Vergleich auf **TSStart selbst** zeigt, dass ein Kartenwechsel für den PASS nicht erforderlich ist. Das Ergebnis stützt die lokale Spawn-/Positionssituation als wesentlichen Einfluss auf den Fehler; ein festes 1200-/1500-Player-Limit erklärt die Befunde nicht. Verteilung und konkrete Positionen ändern allerdings auch die unmittelbare Umgebung einzelner Charaktere. Eine bestimmte Kollisionsroutine, ein NPC oder eine exakte kritische Dichte ist dadurch nicht kausal nachgewiesen. Der ShadowStep-/Koordinatenpfad wurde nicht ausgehebelt oder zusätzlich gepatcht.

## TSStart-Kontrolle: Pilot und vollständige Rampe bestanden

Am 16.10.2026 von **15:05 bis 15:11 MESZ** wurden zunächst 32 Charaktere auf freie, verteilte TSStart-Positionen gesetzt. Die übrigen 1568 blieben auf RouN. Drei Aufnahmestufen bis 32 und zwei Minuten Stabilität bestanden; der Laufzeit-Logaudit war CLEAN, ohne Disconnect 1669. Nach dem Abmelden waren alle 32 Positionen exakt erhalten und alle 32 Loginzähler erhöht.

Der Startaudit meldete REVIEW, weil `Message.txt` beim Neustart gekürzt wurde und die allgemeine Checksum-Regel auf vorhandene SHN-Ladezeilen anschlug. Der vollständige geordnete Vorher-/Nachher-Vergleich ergab ausschließlich zwei entfernte alte Verbindungsversuchszeilen; alle übrigen Zeilen einschließlich der Checksum-Zeilen waren identisch. Diese manuelle Prüfung ist mit beiden Datei-Hashes separat protokolliert. Der ursprüngliche REVIEW-Bericht und `Passed=false` des Gesamtcontrollers bleiben unverändert; der automatische Folgelauf wurde dadurch zunächst nicht ausgelöst.

Ein danach angeforderter erhöhter Hauptlauf wurde zunächst von Windows vor Prozessstart abgebrochen. Alle 1600 Charaktere wurden zwischenzeitlich transaktional auf die gesicherte RouN-Verteilung zurückgesetzt; die exakte Übereinstimmung wurde per SQL geprüft.

Nach erneuter Windows-Freigabe wurde der vollständige Kontrolllauf um **16:26 MESZ** gestartet. Die Rampe lief **16:28–17:31 MESZ**. Alle 26 Stufen bestanden; die letzte Stufe erreichte 1600 ready / 1600 ShinePlayer bei weiterhin 2000 Pool-Slots. Die anschließende Fünf-Minuten-Phase lieferte 60 exakte Samples. Es gab keine endgültigen Clientfehler und keine transienten Zone-Handoff-Retries. Start- und Laufzeit-Audit sind CLEAN mit vollständiger Evidenz, ohne neuen Disconnect 1669.

Die nachträgliche Datenbankprüfung bestätigte alle 1600 Charaktere auf verschiedenen, exakt geplanten TSStart-Positionen, je genau einen zusätzlichen gespeicherten Login und positive HP. Die Kontrolle wurde separat gesichert, weil der ursprüngliche Controller wegen des Login-Stoppfehlers seinen Positionsvergleich übersprungen hatte.

Die gesicherten Positionsdaten verdeutlichen die veränderte lokale Dichte:

| Positionssatz | Verschiedene Positionen | Maximum exakt übereinander | Maximum innerhalb Radius 100 um eine Charakterposition, einschließlich Mittelpunkt |
| --- | ---: | ---: | ---: |
| TSStart vor Änderungen, 1600 Charaktere | 1215 | 128 | 156 |
| RouN nach bestandenem 1600er-Lauf | 1600 | 1 | 13 |
| TSStart nach bestandenem 1600er-Lauf | 1600 | 1 | 12 |

Diese räumlichen Kennzahlen belegen die Entzerrung. Zusammen mit der vollständigen TSStart-Gegenprobe ergibt sich ein belastbarer Betriebsnachweis für verteilte Spawns; die genaue Ursache des ShadowStep-Fehlers bei den alten Positionen bleibt offen.

## Login-Stoppfehler und ehrliche Stop-Prüfung

Nach dem erfolgreichen TSStart-Lastlauf blieb der originale Login-Dienst ab 17:31 MESZ in STOP_PENDING. Zone00 und alle anderen Fiesta-Dienste waren gestoppt. Der Controller meldete deshalb korrekt `Passed=false` mit `CleanupError`; dieser Originalbefund bleibt erhalten. Der Last-PASS und die CLEAN-Logaudits sind getrennte Ergebnisse, kein Beleg für einen erfolgreichen automatischen Dienststopp.

`SmartOrchestrator` ignorierte bisher das Ergebnis seiner 25-Sekunden-Stoppwartezeit und meldete trotzdem „Smart Stop abgeschlossen“. Er wartet nun auch auf bereits stoppende Dienste, begrenzt auf 60 Sekunden, und überprüft am Ende jeden ausgewählten Dienst. Verbleibende Zustände einschließlich STOP_PENDING oder Unknown führen zu einem Fehler; ein erfolgreicher Neustart kann nicht mehr auf diesem falschen Stop-Erfolg aufbauen. Der Manager beendet keine Prozesse zwangsweise.

Die Fehlerstrecke wurde am tatsächlich hängenden Login-Dienst geprüft: Nach 60 Sekunden wird `Smart Stop unvollständig: _Login: StopPending` gemeldet. Die lokale Bereinigung verifizierte anschließend Dienst/PID, Prozesspfad und Startzeit sowie gestoppte übrige Dienste. Ein erster strenger Guard hielt wegen verbliebener TCP-Einträge an; die genauere Prüfung identifizierte ausschließlich zwei unverbundene lokale Bound-Sockets ohne Peer/Listener und keine SQL-Sitzungen. Nur dieser bereits seit Stunden stoppende Test-Login-Prozess wurde daraufhin beendet. Um **20:41 MESZ** waren alle Dienste nachweislich gestoppt; der korrigierte Smart Stop bestand danach auch seine Erfolgsstrecke. Alle 1600 TSStart-Positionen blieben erhalten.

Dieser Befund betrifft die Bereinigung des originalen Login-Dienstes, nicht einen Zone-Pool- oder Aufnahmefehler. Er wird nicht als behobener Login-Binary-Defekt ausgegeben.

## Kaltstart-Wartezeit

Der erste Pilotversuch brach vor Spieleranmeldungen ab, weil AccountLog die bisherige 25-Sekunden-Wartezeit für SCM RUNNING überschritt. Das Dienstlog zeigte erfolgreiche Initialisierung am Ende dieses Fensters; anschließend räumte der Controller auf und stellte die Positionen wieder her. `SmartOrchestrator` wartet nun begrenzt bis zu **60 Sekunden** auf RUNNING. Release-Build: null Fehler/Warnungen. Danach bestanden der erneute Start, der 32er-Pilot und der vollständige 1600er-Lauf einschließlich kontrolliertem Stoppen.

## Lokale Nachweise

`artifacts/zone-density-20261016/` enthält die Sicherungsprüfung, Positionssnapshots und den Pilot; `certification/` enthält Controllerstatus, Startaudit, Runtimeaudit, 26 Stufenergebnisse, Stabilitätsmessungen sowie Positionsvergleich vor/nach dem Lauf. Die unveränderte Zone.exe hat SHA-256 `B8A6688A5648FB39363D7A39B64794DD42095F51D0A4205E40FC332147783EAC`.

`tsstart/pilot/` enthält die kleine Gegenprobe und `startup-manual-review.json`; `tsstart/certification/` den vollständigen Lastlauf und `positions-verification.json`; `tsstart/cleanup-recovery-02/` den kontrollierten Abschluss der Bereinigung. `density-summary.json` enthält die räumlichen Kennzahlen. Der abschließende Profilcheck bestätigt weiterhin 83/83 Patchstellen, bytegenauen inversen Rollback und Listener 2000. Release-Build und separater Publish der Stop-Korrektur bestanden ohne Fehler/Warnungen.

Datenbanksicherungen, Charakterdaten, Zugangsdaten, Serverbinaries und Rohlogs bleiben lokal. Veröffentlicht werden ausschließlich Code und zusammengefasste Prüfergebnisse.

Vorherige Ergebnisse: [Patch-/Rollback-Validierung und TSStart-Fehler](ZONE_MODIFICATION_VALIDATION_2026-10-15.md), [1669-Symbolanalyse](ZONE1669_SYMBOL_ANALYSIS.md).
