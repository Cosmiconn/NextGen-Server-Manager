# NextGen Fiesta Server Manager

Windows-WPF-Manager für Fiesta Online **NA2016**: Service-Steuerung, Smart Start, Log-/PDB-Diagnose, Capacity/Scaling, sichere Zone-Provisionierung und adaptive Hook-/Tuning-Forschung.

## Aktuelle Baseline

**0.3.4 – Hardware Aware** ist die aktuell unter Windows getestete Baseline und liegt auf `main`.

Wichtige Einstiegspunkte:

- `docs/PROJECT_CONTEXT.md` – vollständiger Projekt-/Handoff-Kontext
- `docs/UI_TARGET.md` – verbindliches UI-Ziel und Navigationsstruktur
- `docs/GIT_WORKFLOW.md` – Branch-/PR-Regeln
- `baseline/` – exakter 0.3.4-Quellstand als reproduzierbares Source-Archiv

## Branching

`main` bleibt die getestete Baseline. Neue Funktionen, Tabs und größere UI-Arbeiten erhalten jeweils einen eigenen Branch und werden erst nach Windows-CI und Laufzeittest nach `main` übernommen.

Der erste Arbeitsbranch nach dem Import ist:

`ui/navigation-redesign`

## Windows CI

GitHub Actions baut auf `windows-latest` mit .NET 8. Das Workflow-Script rekonstruiert bei Bedarf den Baseline-Quellstand aus `baseline/`, führt Restore/Build/Publish aus und stellt ein win-x64-Artefakt bereit.

## Lokaler Build

Nach Rekonstruktion bzw. in einem normalen Source-Checkout:

```powershell
.\scripts\Build.ps1
```

Zielplattform: .NET 8 / WPF / win-x64.
