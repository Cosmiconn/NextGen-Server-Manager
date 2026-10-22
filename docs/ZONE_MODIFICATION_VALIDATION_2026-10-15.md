# Zone.exe-Modifikation: Verifikation am 15.10.2026

## Geprüfter GitHub-Stand

- Repository: Cosmiconn/NextGen-Server-Manager.
- Neuester Forschungsstand: research/client-load-simulator, 6499a2d143fb238271f5bed01587a96f297a29e8 (14.10.2026).
- Zugehöriger Windows-Build: https://github.com/Cosmiconn/NextGen-Server-Manager/actions/runs/38600813099 – success.
- develop: 4135565727bfec532a3dc7148685a44563f139f3; Windows-Build 37526348117 – success.
- Keine offenen GitHub-Issues/PRs bei der Abfrage. Dies bedeutet nicht, dass keine Laufzeitprobleme bestehen.
- Der ursprüngliche lokale Ordner war ein Quelltextexport ohne .git. Alle abweichenden vorhandenen Quelldateien wurden als ältere Versionen aus der GitHub-Historie identifiziert.
- Die Korrekturen liegen lokal in .github-review auf research/zone-modification-validation und sind in den Arbeitsordner übernommen. Die davor vorhandenen Dateien werden unter .nextgen-backups gesichert.
- Die Korrekturen wurden als Commit c66467fb7467e8ec7d6567db85be0f3537467072 veröffentlicht: [Draft PR #1](https://github.com/Cosmiconn/NextGen-Server-Manager/pull/1). Windows-Build/WPF-Smoke [38799738050](https://github.com/Cosmiconn/NextGen-Server-Manager/actions/runs/38799738050) und PowerShell-Syntax [38799738010](https://github.com/Cosmiconn/NextGen-Server-Manager/actions/runs/38799738010) sind für genau diesen Commit erfolgreich.

## Reproduzierte und behobene Fehler

1. Binary-Reapply nach Rollback scheiterte immer: Der Verifier erwartete Writer-Sidecars am Rollback-Archiv, die der Rollback nicht erstellt. VerifyAgainstBaseline rekonstruiert nun aus dem registrierten Originalbackup dieselbe zertifizierte Patchmatrix. Ziel-/Originalhash, alle 83 Sites, Safety-Gate und bytegenauer inverser Patch bleiben Pflicht. Die Prüfung neu erzeugter Writer-Kopien verlangt weiterhin ihre Sidecars.
2. Wiederholter Listener-Rollback erzeugte einen Dateinamen, den Reapply nicht akzeptierte. Neue Archive verwenden durchgängig denselben Namensstamm; schon vorhandene Archive im früheren Format werden mit exaktem GUID-Suffix weiterhin akzeptiert.
3. Der Deployment-Selbsttest prüfte einen erfundenen festen Listener-Auditnamen. Er verwendet jetzt die tatsächlichen Ergebnis-Pfade und prüft unterschiedliche Archivnamen und unveränderte Hashes.

## Lokal bestandene Prüfungen

- .NET SDK 8.0.426: Release-Build und win-x64 Publish, 0 Fehler / 0 Warnungen.
- Offline-Writer: 83 Sites, unabhängiger Verifier, bytegenauer Rollback, Originaldatei unverändert.
- Vollständiger Windows-Dateitest auf TEMP-Kopien: Stock -> Patch/Listener 2000 -> Stock -> Reapply -> Stock -> Reapply mit historischem Listener-Archiv -> Stock.
- Negativfälle: falscher Bestätigungstoken, beschädigtes Patcharchiv, beschädigtes Baselinebackup, Stockdatei als Patch und fehlendes Baselinebackup werden abgewiesen.
- WPF-Start/Resize/Navigation: 16 Pflichtmarker und 16 gerenderte Screenshots; einschließlich 720x620.
- Alle 12 im WPF-Smoke ausgeführten Selbsttests bestanden: Headless-Protokoll, Packet-Capture, Zone-Transfer, Client-Capture, CharacterCreate, Identity-Generator, Identity-Provisioning, Spawn-Density-Plan, Ramp-Timing, Logklassifikation, CPU-Affinität und Listener-Zeilenumschreibung.
- Zone1669-Capture-Template sowie Zone00-/Zone01-Launcher-Selbsttests bestanden.
- PowerShell-7-Syntaxprüfung aller Skripte bestanden.
- GUI-Testartefakte: .github-review/artifacts/zone-validation-20261015/startup-smoke.log und ui-smoke/.

Reproduktion (PowerShell 7, keine laufende Zone.exe):

    pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1
    pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ZonePoolOfflineWriter.ps1 -ZoneExe C:/NextGenFiesta/Server/Zone01/Zone.exe
    pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ZonePoolSingleZoneDeployment.ps1 -ZoneExe C:/NextGenFiesta/Server/Zone01/Zone.exe

## Echte Installation, erste Bestandsaufnahme vor dem Live-Test

- Zone00: B8A6688A5648FB39363D7A39B64794DD42095F51D0A4205E40FC332147783EAC.
- Zone01 bis Zone05 und Zone00-Originalbackup: DB1CB42912556A4EA5CDE5C18F15F2495B81465C70CA9C18AD5BC7E36611AFF5.
- Zone00-Verifier: 83/83 Sites, keine Zusatzänderungen, In-Memory-Rollback entspricht exakt Original.
- Zone00-Listener: PG_W00_Z00, nMaxAccept=2000, Backup/Manifest/Hash konsistent.
- Runtime-Observer: WAIT, keine Zone.exe läuft. Kein Runtime-PASS.
- Keine Server-Binary, ServerInfo-Konfiguration oder Testdatenbank wurde für diese Prüfungen verändert.

## Weiterhin offen – Gesamtziel nicht abgeschlossen

- Disconnect 1669 ist im echten Lastlauf erneut reproduziert, aber seine Ursache noch nicht behoben. Es fehlt weiterhin eine FirstHit-Aufzeichnung mit tatsächlichem Rückgabewert, Objektkoordinaten, Mapgrenzen und Aufrufstack.
- Der reale Stock-/Patch-Rückweg ist bestanden. Ein sauberer identischer Stock-Lastvergleich scheitert an der bereits beim Start erschöpften Mob-/NPC-Kapazität. Die erneute strenge 1600er-Rampe wurde ausgeführt und in der 1300er-Stufe durch Disconnect 1669 blockiert; kein neuer 1600er-PASS.
- Der dokumentierte KQ-W2Z-MAKED Buffer-full[0]-Befund ist durch diese Offline-Tests weder erklärt noch behoben.
- Spawn-Density-Versuch benötigt im Spiel verifizierte Positionen und einen echten SQL-/Serverlauf; der Generator-Selbsttest beweist diese nicht.
- Der Betreiber hat den Live-Test einschließlich Serverstart und kontrolliertem Stock-/Patch-Rückweg ausdrücklich autorisiert. Die ausgeführten Tests und ihre Grenzen stehen unten.
- Andere Poolprofile und Live-Prozesspatching sind weiterhin nicht freigegeben.

Der historische 1600er-PASS bleibt ein früherer Kapazitätsbeleg. Er ersetzt weder die aktuelle Laufzeitprüfung noch eine Ursachenanalyse der Disconnects.

## Ergänzung: gesperrte Metadaten beim Rollback

Am 15.10.2026 wurde mit echten Windows-Dateisperren zusätzlich reproduziert:
Der Austausch von Zone.exe bzw. ServerInfo.txt gelang, die Aktualisierung der
JSON scheiterte; dadurch blieb DEPLOYED mit einer bereits zurückgesetzten Datei
stehen. Beide Rollback-Pfade kompensieren diesen Fehler nun aus dem erhaltenen
Auditbackup. Voraussetzung sind unveränderte ursprüngliche Metadaten, bekannte
Hashes von Ziel und Audit sowie weiterhin keine laufende Zone. Unbekannte oder
zwischenzeitlich veränderte Zustände werden nicht überschrieben.

Der erweiterte isolierte Dateitest hält jeweils die JSON mit FileShare.Read
offen, erzwingt damit den Fehler erst beim Metadaten-Commit und prüft:
Fehlermeldung statt falschem Erfolg, erfolgreiche Kompensation, unveränderte JSON,
ursprünglicher Patch-/Listenerhash und anschließend erfolgreicher normaler
Rollback nach Freigabe der Datei. Der vollständige Test mit drei Rollback-Zyklen
bestand danach erneut. Release-Build und Publish: 0 Fehler / 0 Warnungen.

Die getesteten Korrekturen sind auf research/zone-modification-validation mit Draft PR #1 gegen research/client-load-simulator veröffentlicht. Der nachfolgende Live-Test ist davon getrennt dokumentiert.

## Autorisierter Live-Test: Patchprofil und eine echte Anmeldung

Am 15.10.2026, 22:29–22:31 MESZ, wurde die lokale Installation nach einer vollständigen lokalen Sicherung (4.883 Dateien, keine Kopierfehler) tatsächlich gestartet. Ausschließlich die Kerndienste und Zone00 liefen; Zone01–Zone05 blieben gestoppt. Die SQL-Dienste wurden nicht verändert.

- Runtime-Test: PASS. Registrierter Patch-Hash und ShineObjectManager-Maxima Player 2000 / Mob 12000 / NPC 512 im echten Prozess geprüft.
- Belegung vor der Anmeldung: Player 0, Mob 9040, NPC 293. Diese Mob-/NPC-Belegung übersteigt die unveränderten Stock-Limits 8000/256; ein Stock-Vergleich muss deshalb auch die Vollständigkeit der geladenen Welt berücksichtigen.
- Eine vorhandene Testidentität durchlief Login -> WorldManager -> authentischen Zone00-Redirect -> CH6/1 -> SH6/2 -> CH6/3. Keine automatische Charaktererstellung, keine verzögerte Nachaufnahme.
- PASS-Stufe: Ready 1 und exakt ein ShinePlayer in zwei unabhängigen Messungen; anschließend 30 Sekunden Stabilität mit sieben Samples, keine fehlgeschlagene Testidentität.
- Log-Delta während der Anmeldung: CLEAN, sieben geänderte Logdateien, keine neuen erkannten Warn-/Fehler-/Crashindikatoren. Dieser Checkpoint entstand nach dem Serverstart und bewertet dessen frühere Logmeldungen nicht.
- Alle acht gestarteten Fiesta-Dienste wurden anschließend wieder gestoppt; dieser Zustand wurde separat über SCM geprüft.

Lokale Nachweise: artifacts/zone-live-20261015/initial/{state.json,runtime.log,smoke/ramp-result.json,smoke/log-audit.json}. Sicherung, Rohlogs und Testzugänge bleiben lokal und werden nicht veröffentlicht. Dieser Ein-Client-Test ist kein 1600er-Kapazitätsnachweis und reproduziert Disconnect 1669 nicht.

## Echter Stock-/Patch-Rückweg und Stock-Start

Nach erneuter erfolgreicher Windows-Freigabe lief am 15.10.2026, 22:51–22:53 MESZ, der kontrollierte Rückweg auf der tatsächlichen Zone00-Installation:

- Listener auf 1500 und Zone.exe bytegenau auf den ursprünglichen Stock-Hash zurückgesetzt. Auditbackups und registriertes Originalbackup blieben erhalten.
- Stock-Zone gestartet und Runtimeprofil 1500/8000/256 verifiziert. Vor der Anmeldung wurden Player 0, Mob 8000 und NPC 255 gezählt.
- Eine echte Anmeldung und die 30-Sekunden-Stabilität bestanden auch auf Stock. Die getrennte Logprüfung blockierte jedoch: bereits beim Start `NPCManager::nm_SetNPC : Too many npc[0]` und `MobBreeder::mb_regen : Too many mob[0]`; weitere Mob-Limit-Assertions während der Anmeldung.
- Damit ist Stock unter diesen unveränderten Serverdaten kein sauberer Kontrollzustand: Die Welt wird wegen erschöpfter Pools nicht vollständig geladen. Ein Stock-1100-Durchlauf unter dieser abweichenden Weltbelegung würde einen identischen Lastvergleich nicht belegen und wurde nicht als solcher ausgeführt.
- Alle Testdienste gestoppt; ursprünglichen Patch und Listener 2000 erfolgreich wiederhergestellt. Unabhängiger Verifier bestätigte 83/83 Sites, exakten Patch-/Originalhash sowie Listenermanifest, Backup und Hash.
- Anschließend lief die strenge gepatchte 1600er-Rampe ohne Debugger, mit unverändertem Zwei-Sekunden-Starttakt und ohne verzögerte Nachaufnahme. Ergebnis: BLOCKED, siehe folgenden Abschnitt.

Lokale Nachweise: artifacts/zone-live-20261015/stock-preflight/ mit Rollback-, Pool-, Login-, Logaudit- und Reapply-Ergebnissen. Der Original-1669-Debugger wurde nicht gegen die gepatchte Zone erzwungen. Die Poolerschöpfung erklärt die Stock-Startfehler, nicht den separat untersuchten Disconnect 1669.

## Strenge 1600er-Rampe: Disconnect 1669 erneut reproduziert

Die gepatchte Zone lief vom 15.10.2026, 22:53–23:42 MESZ, einschließlich Start und anschließendem Stoppen. Die Rampe selbst lief 22:55–23:42 MESZ mit vorhandenen Konten, authentischen Login-/World-/Zone-Handoffs, Zwei-Sekunden-Starttakt, regulären vier Handoff-Versuchen und ohne verzögerte Nachaufnahme.

- 19 Stufen von 1 bis 1200 bestanden jeweils mit zwei exakten ShinePlayer-Messungen. Dies sind bestandene Aufnahme-/Zählstufen, kein vollständiger Stabilitätsnachweis für 1600.
- Aufbau der Stufe 1300: Testidentität 1220 scheiterte vor SH6/2 nach vier Versuchen. Die zeitgleichen Zone-Logs nennen so_Disconnect 1669; bei Abbruch waren 1299 Clients ready, einer endgültig fehlgeschlagen.
- Der unabhängige Fehler-Snapshot bestätigte weiterhin dieselbe laufende, zertifizierte Zone: Player 1299/2000, Mob 9060/12000, NPC 293/512. ServerMinusReady=0. Der Player-Pool war nicht ausgeschöpft.
- Insgesamt 15 so_Disconnect-1669-Ereignisse bei acht Testidentitäten; sieben erholten sich durch die regulären Handoff-Retries. Das erklärt, warum einzelne frühere Stufen trotz des bereits auftretenden Fehlers ihre Zählprüfung bestanden.
- Neue CoordRepairLog-Einträge korrelieren zeitlich mit diesen Disconnects. Die dort protokollierten TSStart-Referenz-/Reparaturkoordinaten sind weiterhin kein Beleg für die tatsächlichen ungültigen ShadowStep-Koordinaten.
- Ergebnis: LOAD RAMP BLOCKED. Stufen oberhalb 1200 und die finale 1600er-Stabilitätsphase wurden nicht bestanden. Der historische 1600er-Nachweis wird dadurch nicht zu einem aktuellen PASS.
- Danach alle Testdienste gestoppt; Patchhash, 83 Sites und Listener 2000 erneut verifiziert. Keine zusätzlichen Retry-Versuche, keine Map-/Spawn-/Poolanpassung, kein Debugger während dieser Kapazitätsprüfung.

Lokale Nachweise: artifacts/zone-live-20261015/certification/{state.json,ramp/ramp-result.json,ramp/ramp-trace.log,disconnect-evidence/}. Rohdaten und Zugangsdaten bleiben lokal.

## Im Live-Test entdeckter falsch-negativer Logbefund

Die bisherige automatische Logprüfung meldete für diesen fehlgeschlagenen Lauf fälschlich CLEAN. Zwei Ursachen wurden behoben:

1. Die Diagnose-Regeln erkannten die realen Formate `so_Disconnect NAME 1669` und `so_Disconnect[1669]` nicht. NG-ZONE-0023 erkennt beide nun als blockierenden Fehler. Eine erfolgreiche Wiederanmeldung hebt den Befund nicht auf.
2. LogAnalyzer behielt nur den ersten Treffer je Regel. Eine frühe harmlose TS-Debug-Assertion konnte damit spätere echte NG-CORE-0002-Assertions derselben Datei verdecken. Der Delta-Auditor trennt jetzt die Debug-, KQ-Hinweis- und regulären Zeilengruppen vor dieser Zusammenfassung. Die echten 1669-Assertions werden dadurch ebenfalls sichtbar.

Validierung: Release-Build ohne Fehler/Warnungen; Dateitest mit tatsächlicher Logsuche und Checkpoints prüft historische Einträge, andere Disconnect-Gründe, beide 1669-Formate sowie die Folge Debug -> KQ -> echte Assertion (CLEAN -> REVIEW -> BLOCKED). Erneute Auswertung der echten Laufdaten mit dem ursprünglichen Checkpoint ergibt BLOCKED und nennt die 1669-Einträge sowohl im Assert- als auch im MoreDisconnect-Log. Der ursprüngliche falsche CLEAN-Bericht bleibt als Beleg erhalten; die korrigierte Auswertung liegt separat unter certification/corrected-audit/.

Diese Korrektur behebt die Fehlererkennung, nicht den Server-Disconnect selbst. Für dessen Ursachenbehebung fehlt weiterhin die gezielte Laufzeitaufzeichnung des fehlschlagenden Bewegungs-/Markierungspfads.
