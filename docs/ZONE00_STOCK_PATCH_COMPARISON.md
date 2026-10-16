# Zone00: sicherer Stock-/Patch-Vergleich für Disconnect 1669

**Status (15.10.2026):** Release-Build, WPF-Smoke und isolierter Dateitest mit drei Rollback-Zyklen bestanden. Der ausdrücklich autorisierte echte Zone00-Rückweg Stock -> Patch ist ebenfalls verifiziert. Stock startet und erlaubt eine Anmeldung, erschöpft aber unter den vorhandenen Daten bereits seine Mob-/NPC-Pools; die Logprüfung blockiert. Ein sauberer Stock-Lastvergleich ist damit nicht gegeben. Die erneute strenge Patch-Rampe bestand 19 Stufen bis 1200 und scheiterte beim Aufbau auf 1300: 1299 Ready-Clients, ein endgültiger Handoff-Fehler mit Disconnect 1669. Kein neuer 1600er-PASS. Korrekturen und Nachweise: [Verifikationsbericht](ZONE_MODIFICATION_VALIDATION_2026-10-15.md). `main` und der historische 1600-Spieler-Nachweis bleiben unverändert.

**Ausgangslage:** Map-Routing bleibt auf **Zone00**. Ein testweises Umlenken aller Zone00-Maps auf Zone01 wäre ein anderer, unnötiger Eingriff. Der neue Diagnosemodus lässt WorldManager weiterhin die Zone via authentischem `SH4/3` wählen und prüft den gewählten Zone00-Listenerport **vor CH6/1**. Eine reine Portprüfung wird zusätzlich durch die **exakten live-gezählten ShinePlayer von Zone00** abgesichert.

## Bedienoberfläche

Im Bereich **Zone-Pool / Player Load Verification** (Ziel `C:\NextGenFiesta\Server\Zone00\Zone.exe` wählen):

| Aktion | Zweck |
| --- | --- |
| **9 · Komplett-Rollback auf Stock** | Setzt zuerst Zone00-Listener/ServerInfo auf 1500, danach Zone.exe auf bytegenau NA2016-Stock zurück; Hash und Historie bleiben erhalten |
| **C · Zone00-Stock/Patch-Vergleich 1 → 1100** | Nicht zertifizierender Zone00-Diagnoselauf; Stock **oder** Patch. Gleiches Manifest, 2-s-Starttakt, reguläre Handoff-Retries, keine verzögerte Nachaufnahme |
| **10 · Zertifizierten Patch wiederherstellen** | Stellt ursprüngliche zertifizierte Patchdatei **2000/12000/512** und ausschließlich den Zone00-Listener **2000** wieder her |
| **B · Ramp 1 → 1600** | Unveränderter offizieller strenger 1600-Lastnachweis, ausschließlich nach vollständiger Zertifikats-/Runtimeprüfung |

**Noch vor Start aller Tests:** Backup der gesamten Installation/ServerInfo/Logs separat sichern. Aktiven Spielern und laufenden Sitzungen wird beim Stoppen der Zonen die Verbindung genommen. Diese Diagnose gehört ausschließlich auf einen isolierten Testserver.

## Schritt 1: Temporären vollständigen Hin-/Rückweg prüfen (empfohlen)

**Alle Zone.exe-Prozesse müssen beendet sein**, außerdem keine WinDbg-Verbindung offen.

Nach Aktualisierung des Branches Release bauen. Die originale Stock-Binärdatei ist noch bei `C:\NextGenFiesta\Server\Zone01\Zone.exe` vorhanden:

```powershell
cd C:\NextGenServerTools\NextGen-Fiesta-Server-Manager
git fetch origin
git switch research/client-load-simulator
git pull --ff-only
.\scripts\Build.ps1

.\scripts\Test-ZonePoolSingleZoneDeployment.ps1 -ZoneExe "C:\NextGenFiesta\Server\Zone01\Zone.exe"
```

Der Selbsttest verwendet nur eine **temporäre Kopie** und prüft tatsächlich: Stock -> 2000er Patch -> Listener 2000 -> Listener Stock -> Binary Stock -> Binary Reapply -> Listener Reapply -> zweiter vollständiger Stock-Rollback. Jede Binary-Kopie muss genau den bekannten SHA-256 und 83/83 Patchsites aufweisen; in `ServerInfo.txt` darf jeweils ausschließlich die Zone00-Client-Zeile wechseln; alle alten Auditdateien bleiben erhalten. **Ein BUILD SUCCESS ersetzt diesen Windows-Datei-Selbsttest nicht.** Bei Selbsttestfehler hier abbrechen und Log prüfen. Auf FAILURE bleibt das isolierte TEMP-Verzeichnis mit Binary-/Listener-Manifests und Auditkopien zur Diagnose erhalten (Pfad in der Ausgabe); erst nach der Auswertung manuell löschen. Nach SUCCESS wird das TEMP-Verzeichnis automatisch entfernt. PowerShell meldet bei einem fehlgeschlagenen Selbsttest den Exitcode 20.

## Schritt 2: Bestehende Zone00 auf Stock zurücksetzen

1. **WinDbg von allen Zonen trennen.** Laufende Clients beenden; **alle** Zone-Prozesse ordnungsgemäß stoppen. Reine World/Login-Dienste dürfen ggf. noch laufen, aber während des Dateiaustauschs darf keine Zone.exe starten.
2. In der GUI **Zone00** als Ziel auswählen.
3. **9 · Komplett-Rollback auf Stock** wählen und die Rückfrage bestätigen. Die vorhandene Implementierung rollt zwingend erst Listener `2000 → 1500` zurück, dann die exakt zertifizierte Zone.exe `2000/12000/512 → Stock`.
4. Die Statusmeldung **`KOMPLETT-ROLLBACK SUCCESS`** prüfen. Nur wenn der Vorgang komplett erfolgreich war, die Dienste/Zone00 erneut starten.
5. Den unveränderten Map- und Character-Zustand beibehalten. Falls die ursprüngliche Stock-Zone aufgrund zu vieler Mobs/NPCs oder anderen Umgebungsunterschieden nicht startet, **nicht** einfach Limits erhöhen: Logs prüfen und das Experiment als nicht direkt vergleichbar kennzeichnen. Ein offener Client-Port genügt nicht: Im echten Versuch am 15.10. startete Stock mit 8000/8000 Mobs und 255/256 NPCs, meldete aber `Too many npc` und `Too many mob`. Die Anmeldung bestand, die Welt war dennoch nicht vollständig geladen. Bei solchen Start-/Poolfehlern keinen Stock-Lastlauf als identischen Kontrollversuch ausweisen.

## Schritt 3: Original-Zone00 mit WinDbg und Headless-Clients untersuchen

Aus PowerShell als Administrator, während die **isolierte** originale Zone00 läuft:

```powershell
.\scripts\Start-Zone1669Zone00.ps1
```

Das Skript erkennt exakt `Zone00\Zone.exe`, sucht die passende `Zone.pdb` und akzeptiert ausschließlich die originale SHA-/PDB-Identität. Die neue WinDbg-Version zeigt PID und kopiert den zeilenbasierten `$$<`-Aufruf in die Zwischenablage. **Die WinDbg-Kommandos nicht in PowerShell eingeben.** Nur `TESTZONE00` bestätigen, wenn es sich um eine isolierte, nicht produktive Testzone handelt.

In WinDbg die Symbole `lmvm Zone` und den **One-Shot-Breakpoint** per `bl` prüfen. `g` setzt die Zone anschließend fort.

Dann im Server Manager **C · Zone00-Stock/Patch-Vergleich 1 → 1100** starten. Erwartet: echte Login->World->SH4/3->CH6/1->SH6/2->CH6/3-Sessions; je Stufe zwei exakte **Zone00-only** ShinePlayer-Messungen. Andere Zone-Instanzen werden nicht in die Zone00-Zähler gemischt. Der Modus ist absichtlich **nicht** Teil des 1600er-Zertifikats.

Beim ersten Treffer an der 1669-Fehlerstelle schreibt WinDbg `Zone1669_FirstHit_*.log`. Bitte diesen sowie die neuen `player-ramp-trace-*.log`, `Zone00\Assert*.txt`, `MoreDisconnectLog*.txt` und `CoordRepairLog*.txt` für die weitere Analyse sichern. Der Debugger kann Anmeldetimings beeinflussen; den Debugger-Test nicht als unverfälschte Kapazitätsmessung deklarieren.

Wichtig: Der erste bekannte Fehler wurde bei hoher Last beobachtet. Selbst wenn 1100 Spieler erreicht werden, gibt es **keine Garantie**, dass genau derselbe 1669-Pfad noch einmal auftritt.

## Schritt 4: Patch und Listener sicher wiederherstellen

1. Testclient-Sitzungen beenden, WinDbg trennen, **sämtliche Zone.exe-Prozesse stoppen**.
2. In der GUI **10 · Zertifizierten Patch wiederherstellen** auswählen und bestätigen.
3. Der Manager verifiziert Original-/Patch-SHA, das registrierte `ROLLED_BACK`-Deployment samt Originalbackup, die 83 Patchsites, die Listener-Metadaten/Audits und das unveränderte ServerInfo-Stockprofil.
4. Zuerst wird exakt das archivierte **2000/12000/512-Binary** atomar wiederhergestellt, danach exakt die aufgezeichnete **2000er Zone00-Listener-Zeile**. Alle Archive/Records bleiben erhalten; erneute Rollbacks erhalten neue eindeutige Auditpfade statt bisherige Backups zu überschreiben.
5. Nur bei `ZONE00 REPATCH SUCCESS` die Server manuell starten. Danach **6 · Runtime prüfen**, Zone00-Pool `Player=2000/Mob=12000/NPC=512` und Listener 2000 nochmals kontrollieren.

Wenn die Listener-Wiederherstellung scheitert, kompensiert der Manager den Binäraustausch **nur**, wenn der Listener zuvor wieder nachweislich Stock ist. Bei unvollständigem Zustand gilt **nicht starten**; Fehlermeldung/Hashes prüfen.

## Schritt 5: Gepatchten Zone00-Vergleich und Baseline wiederholen

- Auf der gepatchten Zone00 denselben **C-Diagnoselauf** bei gleicher Clientreihenfolge und 2-s-Takt ausführen; die Testumgebung möglichst gleich belassen. Der vorhandene **WinDbg-Stock-Breakpoint-Helper** ist auf den ursprünglichen EXE-SHA gepinnt: **nicht** gegen den Patch erzwingen.
- Danach ohne WinDbg (wenn der Patch und Listener weiter zertifiziert sind) den unveränderten offiziellen **B · Ramp 1 → 1600** zum Vergleich ausführen. Er verlangt 26 PASS-Stufen und die finale Stabilität; keine früheren Erkenntnisse werden durch die C-Diagnose zu einem offiziellen PASS umgedeutet.

## Technische Sicherheit

- Die alten `nextgen-prepatch.bak`, `nextgen-deployment.json`, `nextgen-rollback-patched*.bak`, Listener-Backups/-Manifeste und temporäre Audit-Historie werden weder blind entfernt noch überschrieben.
- Stock `ShinePlayer=1500`, `ShineMob=8000`, `ShineNPC=256`; zertifizierter Patch `2000/12000/512`. Zone00-C-Modus ist aus Vorsicht auf **1100** Clients begrenzt und dadurch **kein** 1600er-Zertifikat.
- Der Standard-Ramp B bewahrt den bisherigen zertifizierten Runtime-Observer und Listenerpreflight.
- Original-WorldRedirect `SH4/3` wird niemals überschrieben; ein abweichender Port ergibt `ZONE_ROUTE_MISMATCH`, **vor** dem Zone-Transfer.
- Der einmalige WinDbg-Hit belegt die tatsächlichen Rückgabewerte/Koordinaten des untersuchten Pfads, nicht automatisch die Ursache aller vorherigen Lastfehler.

Weiterführend: [ZONE1669_RUNTIME_CAPTURE.md](ZONE1669_RUNTIME_CAPTURE.md), [ZONE1669_SYMBOL_ANALYSIS.md](ZONE1669_SYMBOL_ANALYSIS.md), [SCALING_AND_PROVISIONING.md](SCALING_AND_PROVISIONING.md).
