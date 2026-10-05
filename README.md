# NextGen Fiesta Server Manager

Windows-WPF-Manager für Fiesta Online **NA2016**: Service-Steuerung, Smart Start, Log-/PDB-Diagnose, Capacity/Scaling, sichere Zone-Provisionierung und adaptive Hook-/Tuning-Forschung.

## Aktuelle Baseline

**0.3.4 – Hardware Aware** ist die aktuell unter Windows getestete Baseline.

Wichtige Einstiegspunkte:

- `docs/PROJECT_CONTEXT.md` – vollständiger Projekt-/Handoff-Kontext
- `docs/UI_TARGET.md` – verbindliches UI-Ziel und Navigationsstruktur
- `docs/GIT_WORKFLOW.md` – Branch-/PR-Regeln

## Repository-Bootstrap

Das Repository wurde am 05.10.2026 angelegt und die Projekt-/UI-/Workflow-Dokumentation wurde auf `main` übernommen. Der vollständige 0.3.4-Quellimport aus dem Chat-Artefakt ist **noch nicht vollständig im Git-Tree**; der GitHub-Connector kann lokale Binär-/Verzeichnisartefakte nicht direkt als Repository-Datei übernehmen. Die vorhandene `baseline/0.3.4/source.b64.part00` ist nur ein unvollständiger Bootstrap-Teil und noch keine rekonstruierbare Baseline.

Bis der Quellimport vollständig ist, ist der Windows-Build-Workflow als Infrastruktur vorbereitet, aber ein roter Build wegen der fehlenden Baseline ist **kein Code-Regressionsergebnis**.

## Branching

`main` soll nach abgeschlossenem Import die getestete 0.3.4-Baseline tragen. Neue Funktionen, Tabs und größere UI-Arbeiten erhalten jeweils einen eigenen Branch und werden erst nach Windows-CI und Laufzeittest nach `main` übernommen.

Der erste Arbeitsbranch ist:

`ui/navigation-redesign`

## Windows CI

GitHub Actions ist für `windows-latest` + .NET 8 vorbereitet. Sobald der vollständige Quellstand im Repository liegt, wird Restore/Build/Publish dort als verbindlicher Windows-Gate genutzt.

## Lokaler Build

```powershell
.\scripts\Build.ps1
```

Zielplattform: .NET 8 / WPF / win-x64.
