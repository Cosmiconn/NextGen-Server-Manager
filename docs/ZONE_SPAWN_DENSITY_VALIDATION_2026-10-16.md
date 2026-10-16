# Zone00: Pool-Limit und Spawn-Verteilung

Stand: 16.10.2026. Die strenge 1600er-Rampe auf **RouN mit verteilten Positionen ist bestanden**. Das belegt die Aufnahme über das Stock-Limit von 1500 hinaus. Es ist noch keine allgemeine Freigabe für 2000 Spieler oder ein Nachweis, dass Disconnect 1669 auf TSStart behoben ist.

## Vergleich mit dem vorherigen Lauf

| Bedingung | TSStart, bisherige Positionen (15.10.) | RouN, verteilte Positionen (16.10.) |
| --- | --- | --- |
| Zone.exe / Listener | 2000/12000/512, Listener 2000 | identischer Patch und Listener |
| Starttakt / Nachaufnahme | 2 Sekunden / ausgeschaltet | identisch |
| Testidentitäten | vorhandene 1600 Charaktere | dieselben 1600 Charaktere |
| Aufnahme | Stufen bis 1200 bestanden; Abbruch bei 1299 ready / 1 Fehler | alle 26 Stufen bis 1600 bestanden |
| Finale Stabilität | nicht erreicht | 5 Minuten, 60 exakte ShinePlayer-Samples |
| Disconnect 1669 | 15 Ereignisse, 8 Identitäten | keine neuen Ereignisse |
| Laufzeitlogs | BLOCKED nach korrigierter Auswertung | CLEAN, keine Evidenzlücken |
| Startlogs | siehe vorherigen Bericht | CLEAN, keine Evidenzlücken |

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

Das unveränderte Patchprofil **Player 2000 / Mob 12000 / NPC 512** ist in diesem kontrollierten RouN-Szenario mit 1600 gleichzeitigen Sitzungen funktionsfähig. Ein Pool-Maximum allein ist keine Lastzusage. Der Testserver wurde anschließend vollständig gestoppt; die erfolgreich geprüften RouN-Positionen sind zusätzlich gesichert.

Mapwechsel und geringere lokale Spawn-Dichte wurden in diesem Vergleich gemeinsam verändert. Deshalb lässt sich noch nicht entscheiden, welche Bedingung Disconnect 1669 verhindert. Als nächste unabhängige Kontrolle wird eine Verteilung auf **TSStart selbst** geprüft. Der fehlerhafte ShadowStep-/Koordinatenpfad wird nicht ausgehebelt oder durch zusätzliche Pool-Patches verdeckt. Die ursprüngliche TSStart-Positionssicherung bleibt erhalten.

## TSStart-Kontrolle: Pilot bestanden, große Rampe noch offen

Am 16.10.2026 von **15:05 bis 15:11 MESZ** wurden zunächst 32 Charaktere auf freie, verteilte TSStart-Positionen gesetzt. Die übrigen 1568 blieben auf RouN. Drei Aufnahmestufen bis 32 und zwei Minuten Stabilität bestanden; der Laufzeit-Logaudit war CLEAN, ohne Disconnect 1669. Nach dem Abmelden waren alle 32 Positionen exakt erhalten und alle 32 Loginzähler erhöht.

Der Startaudit meldete REVIEW, weil `Message.txt` beim Neustart gekürzt wurde und die allgemeine Checksum-Regel auf vorhandene SHN-Ladezeilen anschlug. Der vollständige geordnete Vorher-/Nachher-Vergleich ergab ausschließlich zwei entfernte alte Verbindungsversuchszeilen; alle übrigen Zeilen einschließlich der Checksum-Zeilen waren identisch. Diese manuelle Prüfung ist mit beiden Datei-Hashes separat protokolliert. Der ursprüngliche REVIEW-Bericht und `Passed=false` des Gesamtcontrollers bleiben unverändert; der automatische Folgelauf wurde dadurch zunächst nicht ausgelöst.

Der danach separat angeforderte erhöhte Hauptlauf wurde von Windows vor Prozessstart abgebrochen. **Es gibt noch keinen 1600er-TSStart-Verteilungsnachweis.** Alle 1600 Charaktere wurden anschließend transaktional auf die separat gesicherte, vollständig geprüfte RouN-Verteilung zurückgesetzt; die exakte Übereinstimmung aller Map-/X-/Y-/Richtungswerte wurde per SQL geprüft. Alle Fiesta-Dienste sind gestoppt.

Nach erneuter Windows-Freigabe wurde der vollständige Kontrolllauf um **16:26 MESZ** gestartet. Der Controller bestätigte vor Dienststart alle 1600 Charaktere auf ihren geplanten TSStart-Positionen. Ergebnis und anschließender Zustand stehen noch aus; der Pilot wird nicht als Ersatz für diesen vollständigen Nachweis gewertet.

Die gesicherten Positionsdaten verdeutlichen die veränderte lokale Dichte:

| Positionssatz | Verschiedene Positionen | Maximum exakt übereinander | Maximum innerhalb Radius 100 um eine Charakterposition, einschließlich Mittelpunkt |
| --- | ---: | ---: | ---: |
| TSStart vor Änderungen, 1600 Charaktere | 1215 | 128 | 156 |
| RouN nach bestandenem 1600er-Lauf | 1600 | 1 | 13 |
| TSStart-Verteilungsplan, erst 32 live geprüft | 1600 | 1 | 12 |

Diese räumlichen Kennzahlen belegen die Entzerrung, nicht die Ursache des ShadowStep-Fehlers. Die vollständige TSStart-Gegenprobe bleibt erforderlich, bevor Mapwechsel und Verteilung voneinander abgegrenzt werden können.

## Kaltstart-Wartezeit

Der erste Pilotversuch brach vor Spieleranmeldungen ab, weil AccountLog die bisherige 25-Sekunden-Wartezeit für SCM RUNNING überschritt. Das Dienstlog zeigte erfolgreiche Initialisierung am Ende dieses Fensters; anschließend räumte der Controller auf und stellte die Positionen wieder her. `SmartOrchestrator` wartet nun begrenzt bis zu **60 Sekunden** auf RUNNING. Release-Build: null Fehler/Warnungen. Danach bestanden der erneute Start, der 32er-Pilot und der vollständige 1600er-Lauf einschließlich kontrolliertem Stoppen.

## Lokale Nachweise

`artifacts/zone-density-20261016/` enthält die Sicherungsprüfung, Positionssnapshots und den Pilot; `certification/` enthält Controllerstatus, Startaudit, Runtimeaudit, 26 Stufenergebnisse, Stabilitätsmessungen sowie Positionsvergleich vor/nach dem Lauf. Die unveränderte Zone.exe hat SHA-256 `B8A6688A5648FB39363D7A39B64794DD42095F51D0A4205E40FC332147783EAC`.

`tsstart/pilot/` enthält die separate kleine Gegenprobe und `startup-manual-review.json`; `density-summary.json` enthält die räumlichen Kennzahlen. Die getesteten Änderungen sind außerdem im erfolgreichen [Windows-Build 38903890664](https://github.com/Cosmiconn/NextGen-Server-Manager/actions/runs/38903890664) für Commit `07411b7c687a64a00a135afa847366d8f2bd1520` geprüft.

Datenbanksicherungen, Charakterdaten, Zugangsdaten, Serverbinaries und Rohlogs bleiben lokal. Veröffentlicht werden ausschließlich Code und zusammengefasste Prüfergebnisse.

Vorherige Ergebnisse: [Patch-/Rollback-Validierung und TSStart-Fehler](ZONE_MODIFICATION_VALIDATION_2026-10-15.md), [1669-Symbolanalyse](ZONE1669_SYMBOL_ANALYSIS.md).
