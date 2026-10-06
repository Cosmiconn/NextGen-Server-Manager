# NA2016 Zone – Pool Audit Index

Stand: 2026-10-06

- Handle-/Rebase-Matrix: `ZONE_HANDLE_REBASE.md`
- Control-Flow-/Allocator-Audit: `ZONE_POOL_CONTROL_FLOW_AUDIT.md`
- Zahlen-Doppelgänger / False Positives: `ZONE_POOL_FALSE_POSITIVES.md`

Aktueller Sicherheitsstand:

- 69 Core-Rebase-Sites bytegenau modelliert,
- 14 zusätzliche Player/Mob/NPC-Abhängigkeiten bytegenau modelliert,
- 13 Control-Flow-/Klassifikationsbelege,
- 7 besonders gefährliche 8000/1500/256-False-Positive-Kontexte explizit geschützt.

Die Pool-Hooks bleiben gesperrt, bis die Vollabdeckung aller relevanten Player/Mob/NPC-Abhängigkeiten als gemeinsames Gate nachgewiesen ist. Kein globales Suchen/Ersetzen von `8000`, `1500` oder `256` ist zulässig.
