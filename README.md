# NextGen Fiesta Server Manager

Native Windows-GUI in C# / .NET 8 / WPF zur Diagnose, Administration, Kapazitätsanalyse und kontrollierten Erweiterung eines Fiesta Online **NA2016** Serverstacks.

## Aktuelle Baseline

**0.3.4 – Hardware Aware** ist die zuletzt real unter Windows getestete Baseline.

Wichtige Einstiegspunkte:

- `docs/PROJECT_CONTEXT.md` – kompletter Projekt-/Handoff-Kontext
- `docs/TEST_STATE_0.3.4.md` – letzter Windows-Teststand vor dem UI-Umbau
- `docs/UI_TARGET.md` – verbindliches, vom Nutzer freigegebenes UI-Ziel
- `docs/GIT_WORKFLOW.md` – Branch-/PR-Regeln
- `docs/ZONE_POOL_HOOK_RESEARCH.md` – aktueller Stand der Pool-/Handle-Forschung

## Repository-Workflow

`main` bleibt die zuletzt abgenommene Baseline. Jede neue Funktion, jeder neue Tab und jeder größere UI-Umbau erhält einen eigenen Branch.

Aktueller nächster Arbeitsbranch:

```text
ui/navigation-redesign
```

Danach z. B.:

```text
feature/gameplay-hooks
feature/optool
research/zone-handle-rebase
fix/<name>
```

Vor jeder Änderung: aktuellen Branch-HEAD, letzte Commits und Windows-CI prüfen. Erst nach grünem Windows-Build und realem Laufzeittest wird nach `main` übernommen.

## Verbindliches UI-Ziel

Der Manager soll am Ende **100 %ig wie das freigegebene Zielbild** wirken: Dark-Blue/Charcoal-Ops-Look, große Hauptkategorien, klare Subtabs, Summary-Cards, Status-Pills und kompakte technische Tabellen. Keine bestehende Funktion darf beim Umbau verloren gehen.

Geplante Navigation:

- Dashboard
- Serverleistung
  - Zone Auslastung / Scaling
  - Performance / Vertical Scaling
  - Limits / Capacity
  - Adaptive Hooks
- Diagnostic
  - Logs & Diagnose
  - Live Timeline
  - PDB / Symbole
- Tools
  - Client / Map Safety
  - später Spielfunktionen / DLL Hook
  - später OPTool
- kleine globale Utilities: Einstellungen, Handbuch, Credits

Siehe `docs/UI_TARGET.md`.

## Aktueller technischer Stand

0.3.4 umfasst unter anderem:

- Service-/PID-/Port-Erkennung für Account, AccountLog, Login, Character, GameLog, WorldManager, GamigoZR und beliebige `ZoneNN`
- Start / Stop / Restart / Reinstall / SCM-Service-Löschung
- Smart Start mit echter WorldManager-Readiness
- rekursive Logerkennung und Live-Timeline
- eigene `NG-*` Diagnosecodes und Windows-Event-Korrelation
- PDB-Symbolindexierung über `llvm-pdbutil`
- Limits / Capacity für Zone und WorldManager
- Client / Map Safety
- Live-Zone-Auslastung und sichere Zone-Provisionierung inklusive Firewall
- Performance / Vertical Scaling
- Hardware-Aware Advisor
- Adaptive Hooks für verifizierte WM-/Config-Grenzen
- weiterhin gesperrte ShinePlayer/ShineMob/ShineNPC-Binary-Hooks bis zur vollständigen 16-Bit-Handle-Rebase-Matrix

## Build

Windows mit .NET 8 SDK:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Check-Prerequisites.ps1
.\scripts\Build.ps1
```

Zielplattform: `.NET 8 / WPF / win-x64`.

GitHub Actions verwendet `windows-latest` und dient als verbindlicher Compile-Gate.

## Nächster technischer Stand nach UI-Abnahme

Nach dem UI-Umbau wird exakt beim dokumentierten 0.3.4-Teststand weitergearbeitet:

1. WM-Anzeige `1500` vs. angewendete `3000` Sessions klären
2. Live-Poolzähler für Zone/WM
3. vollständige 16-Bit-Handle-Rebase-Matrix
4. atomare hashgebundene Pool-Hooks mit Rollback
5. Lastvergleich Test-PC vs. Dell R720
6. Spielfunktions-/DLL-Hook-Modul
7. OPTool-Integration
