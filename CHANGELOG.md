# Changelog

## 0.3.4 – Hardware-aware Scaling & Capacity Correctness

- Added persistent host hardware advisor (CPU model, nominal MHz, logical CPU count, NUMA nodes, host CPU and RAM).
- Added hardware-aware vertical-vs-horizontal recommendation in the Performance tab.
- Fixed diagnostic-code collision: runtime recovery now uses `NG-RUNTIME-0002` instead of the NPC-pool code `NG-ZONE-0020`.
- Removed `Closed ZN from` from hard pool/map capacity events.
- Added source-file freshness check so stale timestamp-less logs do not create new capacity emergencies.
- Extended baseline Zone reverse engineering with 13 verified pool count/stride allocation sites.
- Added Dell R720 CPU guide; 2× E5-2667 v2 is the preferred high-clock Fiesta configuration.

## 0.3.3 – Adaptive Hooks

- Added configurable Adaptive Hook profiles (`Sicher`, `Ausgewogen`, `Leistung / Research`).
- Added whole-PC CPU and physical-RAM sampling in addition to per-process/core pressure.
- Added configurable warn/block thresholds for CPU and RAM.
- Added transactional WorldManager and Zone listener config hooks with backups and rollback.
- Added exact-baseline WorldManager runtime admission hook: reads `m_MaxSessions`/`m_NumSessions` and writes only `g_UserLimit` when the target is valid.
- Added dynamic WorldManager session memory projection using the verified `0x1F7B8` session stride.
- Added hook manifest under `.nextgen-hooks/adaptive-profile.json`.
- Added restore-last-hook-backup action.
- Zone ShinePlayer/Mob/NPC binary targets are configurable and hardware-rated, but remain guarded until the complete 16-bit handle rebase dependency graph is verified.

## 0.3.2 - Vertical Scaling & Performance Audit

- Neuer Performance-/Vertical-Scaling-Tab.
- Prüft PE32/LARGE_ADDRESS_AWARE für Serverkomponenten.
- Erkennt per PDB Hinweise auf IOCP-Worker und zentrale Mainthreads.
- Bewertet pro Prozess CPU-Core-Last, RAM, Threads, Handles und Sessions.
- Zeigt konservative vertikale Reserve statt nur "mehr Zonen" zu empfehlen.
- Tuning-Kandidaten: WM Client/Zone-Sessions, ShinePlayer, ShineMob, ShineNPC, MapBlockInformation, MapCluster.
- Berechnet zusätzlichen Rohspeicher für bekannte Objektpool-Erhöhungen.
- Keine automatischen Binary-Patches; alle riskanten Änderungen bleiben Dry-Run/Planung.


## 0.3.1 - Live Capacity & Zone Scaling

- measure established client sessions per Zone client listener
- add core-equivalent CPU plus Private/Virtual memory metrics
- combine client, CPU, private-memory and configured-map pressure into per-zone capacity status
- keep a ten-minute in-memory trend window for early scaling warnings
- connect hard pool/map overflow log diagnostics to capacity status
- add safe next-Zone planner with automatic free three-port block
- add transactional ZoneNN provisioning with ServerInfo backup, config rewrite, native service registration and recovery policy
- add inbound Windows Firewall rule for the new client port only
- never expose internal/OPTool ports automatically; never auto-start or auto-reassign maps after provisioning
- correct ServerInfo parser naming to nBackLog / nMaxAccept

# 0.3.0 – Client Terrain / Map Safety

- prüft `Fiesta.bin` automatisch bzw. über auswählbaren Client-Pfad
- erkennt die bekannte NA2016 Client-Baseline per SHA-256
- prüft PE32/LARGE_ADDRESS_AWARE und zehn Terrain-/HTD-Signaturen
- dokumentiert den statisch analysierten dynamischen HeightMap/HTD-Loader
- neue Größenmatrix 64..4096 mit Height-Punkten, Weltseite, Chunks, HTD/SHBD und rohem Height-RAM
- trennt Terrain-Formatfähigkeit von Gameplay-/Koordinaten-/Protocol-Sicherheit
- 655/656-Warnschwelle als explizite signed-16-bit-Inferenz bei 50 Units/Quad
- 512×512 als konservatives stock-NA2016-Produktionsziel; 1024 ausdrücklich nicht als stock-sicher
- `Field.txt`-Parser korrigiert: leere/reservierte Tab-Spalten bleiben erhalten und Spalten werden über `#ColumnName` aufgelöst
- Client-/Terrain-Ergebnisse werden im JSON-Diagnoseexport gespeichert

# 0.2.9 – Capacity / Map Limits

- neuer Tab **Limits / Capacity**
- harte ShineObjectManager-Pools aus Zone.exe dokumentiert: Player 1500, Mob 8000, NPC 256 u.a.
- BlockDistributeManager-Limit 64 ergänzt
- rekursive SHBD-Inventur aus `9Data/Shine/BlockInfo`
- SHBD-Header/Payload-Validierung und Kollisionsgrid-Berechnung
- Field.txt xsize/ysize und Zone-Zuweisung werden gegenübergestellt
- größte vorhandene Karte/Blockfläche wird automatisch ermittelt
- Capacity-Daten werden in den JSON-Diagnosebericht exportiert

# Changelog

## 0.2.5 - Runtime binding fix

- Fixed WPF startup crash caused by default TwoWay binding to read-only/private-set ViewModel properties.
- Added explicit OneWay bindings for LogText, ActivityText and PdbOutput.
- Set status/diagnostic DataGrids to read-only.

# Changelog

## 0.2.3 - Global IO / Windows build fix

- adds project-wide `GlobalUsings.cs` with `System.IO` and common base namespaces so `File`, `Path`, `Directory`, `SearchOption`, `FileStream`, `StreamReader`, `IOException`, `FileMode`, `FileAccess` and `FileShare` resolve consistently under the Windows .NET 8 build
- fixes `WindowsServiceManager` PID inference by making the ternary result explicitly `int?`
- keeps the 0.2.2 WPF-only namespace cleanup (`System.Windows.Application`, `System.Threading.Timer`)
- addresses the 99-error build log where almost all diagnostics were cascades of the missing IO namespace
- version bumped to 0.2.3

## 0.2.2 - Windows Build Namespace Fix

- entfernt die unnötige Windows-Forms-Projektaktivierung und nutzt unter .NET 8 den nativen WPF `OpenFolderDialog`
- beseitigt den `Application`-Namenskonflikt zwischen WinForms und WPF
- qualifiziert `System.Threading.Timer` im Live-Log-Monitor
- ergänzt explizit `System.IO` für `FileSystemWatcher`
- Versionsnummern auf 0.2.2 angehoben
- keine funktionalen Änderungen am NA2016-Diagnosemodell

## 0.2.1 - Build fix

- Fixed `CS8997` in `ConfigurationAuditor.cs` by correcting the multiline interpolated raw string used for generated `ZoneServerInfo.txt`.
- `Build.ps1` now fails fast after restore/build/publish errors instead of continuing and printing duplicate compiler diagnostics.
- Added `BUILD_FIX_0.2.1.md` with exact rebuild steps.

## 0.2 – Live Diagnostics

- Live-Tail für NA2016 Logs mit Rotationserkennung
- Live Timeline für Log- und Runtime-Ereignisse
- CPU/RAM/Handles/Threads und Port-PID-Prüfung
- Crash-Loop-, Reconnect-Burst- und Startup-Race-Korrelation
- Windows Event Log Diagnose
- Smart Start wartet auf WM-Port **und** aktuellen RUNNING-Logmarker
- 3-Sekunden-Stabilitätsprüfung pro Zone
- JSON Diagnosebericht-Export
- detaillierte deutsche Build-/Download-Anleitung mit aktuellen offiziellen Links
- Build-Quickstart + winget-Installer für .NET 8 / optional Visual Studio / LLVM
- Prerequisite-Check und Self-contained Publish-Script

## 0.1 – MVP

- Service Discovery / Lifecycle
- ServerInfo / ZoneServerInfo Audit
- Smart Start mit Port-Readiness
- Logregeln / NG-Fehlercodes
- Referenzhash-Audit
- optionale PDB-Symbolindexierung

## 0.2.4
- Add guarded WPF startup and persistent startup crash log.
- Add global exception diagnostics and startup failure dialog.
- Add Run-Startup-Diagnostics.ps1 for immediate-exit troubleshooting.

## 0.2.6
- Auto-detect LLVM/llvm-pdbutil outside PATH.
- Fix duplicate Zone.pdb symbol-cache overwrites.
- Show resolved llvm-pdbutil path after successful indexing.


## 0.2.7
- Fix PDB symbol search command enablement.
- Add Enter-to-search and result counters.
- Recursively discover PDB files below Server Root.
- Document verified NA2016 Zone/WorldManager limits.

## 0.2.8
- Recursive service log discovery instead of only a few top-level names.
- Detect log/debug/error/crash/trace folders and additional text log extensions.
- Recursive live FileSystemWatcher with polling fallback for rotated/new logs.
- Full analysis widened from 4 x 1200 lines to configurable defaults of 32 logs x 2500 tail lines per service.
- Add log inventory count in the GUI and analysis statistics in the status line/export.
