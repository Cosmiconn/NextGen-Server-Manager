# Zone.exe-Modifikation: Verifikation am 15.10.2026

## Geprüfter GitHub-Stand

- Repository: Cosmiconn/NextGen-Server-Manager.
- Neuester Forschungsstand: research/client-load-simulator, 6499a2d143fb238271f5bed01587a96f297a29e8 (14.10.2026).
- Zugehöriger Windows-Build: https://github.com/Cosmiconn/NextGen-Server-Manager/actions/runs/38600813099 – success.
- develop: 4135565727bfec532a3dc7148685a44563f139f3; Windows-Build 37526348117 – success.
- Keine offenen GitHub-Issues/PRs bei der Abfrage. Dies bedeutet nicht, dass keine Laufzeitprobleme bestehen.
- Der ursprüngliche lokale Ordner war ein Quelltextexport ohne .git. Alle abweichenden vorhandenen Quelldateien wurden als ältere Versionen aus der GitHub-Historie identifiziert.
- Die Korrekturen liegen lokal in .github-review auf research/zone-modification-validation und sind in den Arbeitsordner übernommen. Die davor vorhandenen Dateien werden unter .nextgen-backups gesichert.
- Die neuen Korrekturen sind lokal getestet; die oben genannten GitHub-CI-Ergebnisse gelten für den unveränderten Ausgangscommit, nicht für diese Korrekturen.

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

## Echte Installation, ausschließlich gelesen

- Zone00: B8A6688A5648FB39363D7A39B64794DD42095F51D0A4205E40FC332147783EAC.
- Zone01 bis Zone05 und Zone00-Originalbackup: DB1CB42912556A4EA5CDE5C18F15F2495B81465C70CA9C18AD5BC7E36611AFF5.
- Zone00-Verifier: 83/83 Sites, keine Zusatzänderungen, In-Memory-Rollback entspricht exakt Original.
- Zone00-Listener: PG_W00_Z00, nMaxAccept=2000, Backup/Manifest/Hash konsistent.
- Runtime-Observer: WAIT, keine Zone.exe läuft. Kein Runtime-PASS.
- Keine Server-Binary, ServerInfo-Konfiguration oder Testdatenbank wurde für diese Prüfungen verändert.

## Weiterhin offen – Gesamtziel nicht abgeschlossen

- Reale Reproduktion und Ursachenbehebung von Disconnect 1669. Im untersuchten Capture-Ordner lagen nur vorbereitete Debugger-Skripte, kein FirstHit-Log.
- Kontrollierter Stock-/Patch-Vergleich unter identischer Last, anschließend erneute 1600er-Rampe, Runtime-/Stabilitäts- und Logprüfung.
- Der dokumentierte KQ-W2Z-MAKED Buffer-full[0]-Befund ist durch diese Offline-Tests weder erklärt noch behoben.
- Spawn-Density-Versuch benötigt im Spiel verifizierte Positionen und einen echten SQL-/Serverlauf; der Generator-Selbsttest beweist diese nicht.
- Vor Serverstart/Dateiaustausch/Lastanmeldungen ist zu bestätigen, dass die lokale Installation isoliert für diesen Versuch genutzt werden darf. Die Rückfrage wurde gestellt; ohne Antwort wurde dieser Teil nicht ausgeführt.
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

Die Veröffentlichung dieser getesteten Korrekturen erfolgt auf
research/zone-modification-validation mit einem PR gegen
research/client-load-simulator. Die Rückfrage zur isolierten Serverinstallation
ist weiterhin offen; eine GitHub-Veröffentlichung ist kein neuer Runtime-PASS.
