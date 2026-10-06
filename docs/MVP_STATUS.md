# Status – Live Diagnostics 0.2

## Implementiert

- dynamische Core-/Zone-Erkennung
- Service Lifecycle: Start, Stop, Restart, Delete, Reinstall, Recovery
- ServerInfo/ZoneServerInfo-Audit
- Referenz-Hash-Audit der NA2016-Binaries
- Smart Start mit WM-Port-Readiness
- **WM-Ready-Marker aus aktuellem WorldManager-Log**
- sequentieller Zone-Start + Stabilitätsfenster + einmaliger Recovery-Versuch
- Log-Regeln + NG-Fehlercodes
- dep/cmd → Opcode-Auflösung für wichtige bekannte Pakete
- FileSystemWatcher + Polling Live-Tail
- Service-/PID-Transition-Timeline
- CPU/RAM/Handles/Threads
- Client-Port → PID Zuordnung
- Reconnect-Burst-, Crash-Loop- und Startup-Race-Korrelation
- Windows Application/System Event Log Diagnose
- optionale llvm-pdbutil Symbolindexierung
- deutsche Build-/Download-Anleitung + Prerequisite-Check

## Noch nicht vollständig

- Crashdump-Minidump-Auswertung
- vollständiger PDB Enum-/Struct-Katalog
- PE/PDB GUID-Abgleich
- kompletter Reference-Dateibaum-Diff
- Zone Add/Clone Wizard
- exportierbarer HTML/JSON Diagnosebericht
- automatisierter Windows-CI-Build
