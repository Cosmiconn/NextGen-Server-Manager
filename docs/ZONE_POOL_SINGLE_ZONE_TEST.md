# Zone Pool 2000/12000/512 – Ein-Zonen-Test-Runbook

Stand: 2026-10-06

Dieses Runbook beschreibt ausschließlich den kontrollierten Test des aktuell zertifizierten NA2016-Zone-Profils:

- ShinePlayer: 2.000
- ShineMob: 12.000
- ShineNPC: 512

Der Pfad ist **nicht** als generischer Binary-Patcher freigegeben. Andere Profile bleiben Analyse-only. Live-/In-Place-Patching im laufenden Prozess bleibt gesperrt.

## Verifizierte Hashes

- NA2016 Baseline `Zone.exe`: `DB1CB42912556A4EA5CDE5C18F15F2495B81465C70CA9C18AD5BC7E36611AFF5`
- Zertifizierte 2000/12000/512 Patchkopie: `B8A6688A5648FB39363D7A39B64794DD42095F51D0A4205E40FC332147783EAC`
- Erwartete geänderte Sites: `83`

## Harte Sicherheitsregeln

1. Während Writer-, Preflight-, Deployment- oder Rollback-Schritten darf **kein einziger `Zone.exe`-Prozess** laufen.
2. Die originale Baseline wird vor jeder Schreiboperation erneut gehasht.
3. Die Patchkopie muss den gepinnten Ziel-SHA besitzen und den unabhängigen Patched-Copy-Verifier bestehen.
4. Das echte Testdeployment darf nur auf genau eine `Zone.exe` angewendet werden, deren Hash exakt der verifizierten Baseline entspricht.
5. Der Austausch erfolgt atomar und erzeugt zwingend `Zone.exe.nextgen-prepatch.bak` plus `Zone.exe.nextgen-deployment.json`.
6. Der Deployment-Befehl startet **keinen** Zone-Prozess.
7. Bei Fehlern nach dem Dateiaustausch versucht der Deployment-Service automatisch einen Notfall-Rollback auf die Baseline.
8. Vor einem echten Serverstart muss der Ziel-SHA erneut dokumentiert werden.
9. Nach jedem Test muss entweder der kontrollierte Rollback erfolgreich sein oder der Test bleibt gestoppt, bis der Dateistand manuell geklärt ist.

## 1. Release bauen

Im Repository-Root:

```powershell
.\scripts\Build.ps1
```

Der Standard-Publish-Ordner ist:

```text
src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\win-x64\publish
```

## 2. Alle Zone-Prozesse stoppen

Vor den folgenden Schritten sicherstellen:

```powershell
Get-Process Zone -ErrorAction SilentlyContinue
```

Die Ausgabe muss leer sein. Wenn noch eine Zone läuft, **nicht fortfahren**.

## 3. Isolierten Offline-Writer-Selbsttest ausführen

Dieser Test verändert die angegebene `Zone.exe` nicht und arbeitet nur in `%TEMP%`:

```powershell
.\scripts\Test-ZonePoolOfflineWriter.ps1 `
  -ZoneExe 'C:\NextGenFiestaServer\Zone00\Zone.exe'
```

Erwartung:

- `SELFTEST: SUCCESS`
- Baseline-SHA = `DB1CB429...AFF5`
- Patch-SHA = `B8A6688A...83EAC`
- Rollback-SHA = Baseline-SHA

## 4. Vollständigen Deployment/Rollback-End-to-End-Selbsttest ausführen

Dieser Test kopiert die Baseline zuerst nach `%TEMP%`, erzeugt dort die Patchkopie, führt dort den atomaren Austausch aus und rollt dort wieder zurück. Der echte Serverordner wird nicht verändert.

```powershell
.\scripts\Test-ZonePoolSingleZoneDeployment.ps1 `
  -ZoneExe 'C:\NextGenFiestaServer\Zone00\Zone.exe'
```

Erwartung:

- `DEPLOYMENT SELFTEST: SUCCESS`
- Deployed-SHA = `B8A6688A...83EAC`
- Rolled-back-SHA = `DB1CB429...AFF5`
- Original-Baseline unverändert

**Ohne erfolgreichen Schritt 4 kein echtes Ein-Zonen-Testdeployment.**

## 5. Zertifizierte Patchkopie erzeugen

Ausgabe bewusst außerhalb des aktiven Zone-Verzeichnisses ablegen:

```powershell
.\scripts\New-ZonePoolPatchedCopy.ps1 `
  -ZoneExe 'C:\NextGenFiestaServer\Zone00\Zone.exe' `
  -Output 'C:\NextGenZonePoolTest\Zone.NextGen-2000-12000-512.exe'
```

Erwartung:

- `OFFLINE COPY: SUCCESS`
- Patch-SHA exakt `B8A6688A...83EAC`
- `Sites geaendert: 83`
- Sidecars `.baseline.bak` und `.nextgen-patch.json` vorhanden

## 6. Patchkopie unabhängig rückprüfen

```powershell
.\scripts\Test-ZonePoolPatchedCopy.ps1 `
  -PatchedZoneExe 'C:\NextGenZonePoolTest\Zone.NextGen-2000-12000-512.exe'
```

Nur bei vollständigem `VERIFIED` fortfahren.

## 7. Read-only Deployment-Preflight gegen die echte Test-Zone

```powershell
.\scripts\Test-ZonePoolDeploymentReadiness.ps1 `
  -TargetZoneExe 'C:\NextGenFiestaServer\Zone00\Zone.exe' `
  -PatchedZoneExe 'C:\NextGenZonePoolTest\Zone.NextGen-2000-12000-512.exe'
```

Erwartung:

```text
DEPLOYMENT PREFLIGHT: READY
```

Dieser Schritt verändert noch keine Datei.

## 8. Ein-Zonen-Testdeployment

Noch einmal prüfen, dass keine Zone läuft:

```powershell
Get-Process Zone -ErrorAction SilentlyContinue
```

Dann ausschließlich mit dem expliziten Bestätigungstoken:

```powershell
.\scripts\Deploy-ZonePoolSingleZoneTest.ps1 `
  -TargetZoneExe 'C:\NextGenFiestaServer\Zone00\Zone.exe' `
  -PatchedZoneExe 'C:\NextGenZonePoolTest\Zone.NextGen-2000-12000-512.exe' `
  -Confirm 'DEPLOY-CERTIFIED-ZONE-POOL-2000-12000-512'
```

Erwartung:

- `TESTDEPLOYMENT: SUCCESS`
- Ziel-SHA = `B8A6688A...83EAC`
- Backup-SHA = `DB1CB429...AFF5`
- `Zone.exe.nextgen-prepatch.bak` vorhanden
- `Zone.exe.nextgen-deployment.json` vorhanden und `State = DEPLOYED`
- **noch keine Zone gestartet**

## 9. Vor dem ersten Start manuell dokumentieren

```powershell
Get-FileHash 'C:\NextGenFiestaServer\Zone00\Zone.exe' -Algorithm SHA256
Get-FileHash 'C:\NextGenFiestaServer\Zone00\Zone.exe.nextgen-prepatch.bak' -Algorithm SHA256
Get-Content 'C:\NextGenFiestaServer\Zone00\Zone.exe.nextgen-deployment.json' -Raw
```

Nur wenn Ziel-/Backup-Hashes exakt den oben genannten Werten entsprechen, darf der eigentliche Runtime-Test beginnen.

## 10. Runtime-Test – noch bewusst manuell

Der Manager startet die gepatchte Zone in diesem Forschungsstand **nicht automatisch**. Für den ersten Test soll genau eine Zone kontrolliert gestartet und sofort beobachtet werden.

Zu prüfen:

- Prozess bleibt stabil und beendet sich nicht unmittelbar.
- Startup-Logs enthalten keine neuen Parser-, Allocator-, Handle- oder Poolfehler.
- Die Runtime-Kapazitätsmessung meldet für die getestete Zone die erwarteten Maxima:
  - ShinePlayer `2000`
  - ShineMob `12000`
  - ShineNPC `512`
- Live-Belegung bleibt plausibel und überschreitet keine Maxima.
- Handle-/Objektfehler, `Too many ...`, Allocation-Fehler oder ungewöhnliche Access-Violations führen zum sofortigen Abbruch des Tests.

Erst nach erfolgreichem Runtime-Test kann über eine breitere Deployment-Automatisierung nachgedacht werden.

## 11. Kontrollierter Rollback

Alle Zone-Prozesse wieder stoppen. Anschließend:

```powershell
.\scripts\Rollback-ZonePoolSingleZoneTest.ps1 `
  -TargetZoneExe 'C:\NextGenFiestaServer\Zone00\Zone.exe' `
  -Confirm 'ROLLBACK-CERTIFIED-ZONE-POOL-TEST'
```

Erwartung:

- `TESTROLLBACK: SUCCESS`
- Ziel-SHA wieder `DB1CB429...AFF5`
- gepatchter vorheriger Stand als `Zone.exe.nextgen-rollback-patched.bak` erhalten
- Deployment-JSON `State = ROLLED_BACK`

Danach erneut verifizieren:

```powershell
Get-FileHash 'C:\NextGenFiestaServer\Zone00\Zone.exe' -Algorithm SHA256
```

## Aktuelle Freigabegrenze

Statisch zertifiziert und als Offline-/Ein-Zonen-Testpfad implementiert:

- 16-Bit-Handle-Rebase-Matrix
- 69 Core-Sites
- 14 zusätzliche Allocation-/Count-/Loop-/Diagnostic-Sites
- insgesamt 83 geänderte Sites
- gepinnter Baseline- und Ziel-SHA
- unabhängiger Patchkopie-Verifier
- atomarer Testdeployment-Pfad
- Baseline-Backup, Deployment-Metadaten und atomarer Rollback
- isolierter Deployment/Rollback-End-to-End-Selbsttest

Noch **nicht** freigegeben:

- automatisches Starten der gepatchten Zone direkt durch den Deployment-Befehl
- paralleles Deployment auf mehrere Zonen
- andere Player/Mob/NPC-Zielprofile
- Live-Prozesspatching
- In-Place-Patching ohne Backup/Transaktion
- Produktionseinsatz ohne erfolgreichen realen Runtime-Test
