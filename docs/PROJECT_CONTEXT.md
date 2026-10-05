# NextGen Server Manager – Projektkontext / Handoff

Stand: **2026-10-05**  
Baseline: **0.3.4 Hardware Aware**  
Repository: **Cosmiconn/NextGen-Server-Manager**  
Baseline-Branch: **main**

Dieses Dokument ist die zentrale Wiedereinstiegsquelle. Ein neuer Chat/Entwickler soll damit ohne Rekonstruktion aus alten Gesprächen weiterarbeiten können.

---

## 1. Projektziel

Native Windows-GUI in C#/.NET 8/WPF zur Diagnose, Administration, Kapazitätsanalyse und kontrollierten Erweiterung eines Fiesta Online **NA2016** Serverstacks.

Kernziele:

- Services erkennen, starten, stoppen, neu starten und neu registrieren
- Smart Start mit echter WorldManager-Readiness
- Logs rekursiv lesen und korrelieren
- eigene `NG-*` Diagnosecodes
- PDB/Symbolanalyse
- Runtime-Timeline / Windows Event Log
- statische und Live-Kapazitätsanalyse
- sichere Zone-Provisionierung inkl. Ports und Firewall
- Hardware-/Vertical-Scaling-Beratung
- Adaptive Hooks
- später vollständig verifizierte Zone-Pool-Hooks sowie Spielfunktions-/DLL-Hooks
- langfristig OPTool-Integration

---

## 2. Verbindliche Entwicklungsregeln

1. `main` ist die zuletzt bekannte, unter Windows getestete Baseline.
2. Jede neue Funktion, jeder neue Tab und jeder größere UI-Umbau bekommt einen eigenen Branch.
3. Vor Änderungen aktuellen Branch-HEAD und Windows-CI prüfen.
4. Nie neuere parallele Änderungen überschreiben.
5. Bestehende Funktionen dürfen bei UI-Umbauten nicht entfernt oder unzugänglich werden.
6. Riskante Binary-/Hook-Änderungen müssen hash-/buildgebunden, transaktional und rollback-fähig sein.
7. Keine EXE blind anhand einzelner Konstanten patchen.
8. Reale Windows-Laufzeittests und GitHub Actions sind maßgeblich; statische Linux-Prüfung ist kein Ersatz.

Siehe `docs/GIT_WORKFLOW.md`.

---

## 3. Verbindliches UI-Ziel

Die vom Nutzer am 05.10.2026 freigegebene Mockup-Ansicht ist das visuelle Ziel. Die UI soll am Ende möglichst exakt diesem Aufbau entsprechen.

Gewünschte Informationsarchitektur:

- **Dashboard**
- **Serverleistung**
  - Zone Auslastung / Scaling
  - Performance / Vertical Scaling
  - Limits / Capacity
  - Adaptive Hooks
- **Diagnostic**
  - Logs & Diagnose
  - Live Timeline
  - PDB / Symbole
- **Tools**
  - Client / Map Safety
  - später Spielfunktionen / DLL Hook
  - später OPTool
- kleine globale Utility-Buttons außerhalb der Haupttab-Leiste:
  - Einstellungen
  - Handbuch
  - Credits

Der nächste Arbeitsbranch ist `ui/navigation-redesign`.

Siehe `docs/UI_TARGET.md`.

---

## 4. Aktuell getesteter Stand 0.3.4

0.3.4 wurde unter Windows gestartet und getestet.

Test-PC laut Performance-Tab:

- **AMD Ryzen 5 3600 6-Core Processor**
- 12 logische CPUs
- 1 NUMA-Node
- ungefähr 3,59 GHz nominal
- ungefähr 15,9 GB RAM

Beobachtung ohne reale Clientlast:

- Zone00/Zone01/Zone02 liegen bereits grob im Bereich **72–79 % eines CPU-Kerns**.
- Private RAM dieser belasteten Zonen liegt grob bei **1,9–2,1 GB pro Prozess**.
- Zone04/Zone05 zeigen wesentlich geringere CPU-Grundlast.
- Zone03 ist weiterhin gestoppt.
- WorldManager liegt bei sehr geringer CPU-Last und etwa **478 MB Private RAM**.

Interpretation: Der Test-PC liegt bei den stark belegten Zonen bereits im Bereich eines **Single-Core/Mainthread-Limits**. Gesamt-CPU alleine darf deshalb niemals die Tuningentscheidung steuern.

### Adaptive Hooks – getesteter Zustand

Profil **Ausgewogen** wurde angewendet.

Sichtbare Ziel-/Ist-Werte:

- WM Client Sessions: **3000 / 3000**
- WM S2S: **150 / 150**
- Zone Listener: **1500 / 1500**
- ShinePlayer Ziel 2000: **BLOCKIERT**
- ShineMob Ziel 12000: **BLOCKIERT**
- ShineNPC Ziel 512: **BLOCKIERT**

Die Zone-Binary-Hooks bleiben absichtlich gesperrt, bis die komplette 16-Bit-Handle-Rebase-Matrix verifiziert ist.

### Offener Konsistenzpunkt

Andere Ansichten zeigen teilweise weiterhin `WorldManager Clients 0/1500`, während Adaptive Hooks 3000/3000 meldet. Nach dem UI-Umbau muss geklärt werden, ob dies:

- ein erst nach WM-Neustart aktiver Hard-Pool,
- ein alter Anzeige-/Configwert,
- oder eine ViewModel-Konsistenzlücke

ist. Nicht kosmetisch überschreiben; Quelle der Werte klären.

---

## 5. Bereits verifizierte NA2016-Limits

### Zone

- Client Listener `nMaxAccept`: **1500**
- ShinePlayer: **1500**
- ShineMob: **8000**
- ShineNPC: **256**
- MapCluster Registry: **512**
- MapBlockInformation: **256**
- BlockDistribute: **64**
- KQ Entrance: **100**

### WorldManager

- Stock Client Session Pool: **1500**
- Stock Zone/S2S Session Pool: **100**
- OPTool Sessions: **30**
- KingdomQuest Container: **300**
- Guild Count: **16384**

Der WM-Clientsessionmanager ist dynamisch aufgebaut (`InitSessions(maxSessions)`, `m_MaxSessions`, `m_NumSessions`, `g_UserLimit`). Für den exakt verifizierten Build kann `g_UserLimit` live gesetzt werden; die harte Sessionpoolgröße entsteht beim Start.

---

## 6. Zone-Pool-/Hook-Forschung

Der zentrale Poolinitialisierungsblock ist lokalisiert. Verifizierte Count/Stride-Paare:

- ShineMob `8000 × 0x2568`
- ShinePlayer `1500 × 0x2C058`
- ShineNPC `256 × 0x256C`
- ShineBandit `2048 × 0x266C`
- ShineDoor `1000 × 0x1F14`
- ShineMagicField `250 × 0x01C8`
- ShineMiniHouse `1000 × 0xD100`
- ShineServant `500 × 0x2598`
- ShineMover `1000 × 0x202C`
- ShinePet `1500 × 0x25D4`
- ShineDropItem `3000 × 0x028B`
- ShineEffectObject `1000 × 0x01D3`
- ShineAxialFlag `3584 × 0x0188`

### Kritische Erkenntnis

Die Objektklassen teilen einen 16-Bit-Handle-Raum. Eine Poolvergrößerung verändert daher Basen/Ranges nachfolgender Klassen. `8000 -> 12000 Mobs` darf nicht isoliert gepatcht werden.

Nächster technischer Forschungsblock **nach der UI-Umstellung**:

1. vollständige Handle-Rebase-Matrix rekonstruieren
2. alle abhängigen Range Checks / Base Offsets / Schleifengrenzen verifizieren
3. atomare, hashgebundene Patchprofile bauen
4. Rollback und Preflight implementieren
5. Live-Poolzähler aus Zone/WM ergänzen
6. erst dann ShinePlayer/ShineMob/ShineNPC freischalten

---

## 7. Karten-/Client-Limits

Serverseitig wurden dynamische SHBD-/Map-Pfade und reale große Karten analysiert. Wichtig ist die Trennung von Field-Metadaten, SHBD-Grid und Client-Terrain.

Für den unveränderten NA2016-Client gilt weiterhin **512×512 Terrain-Quads** als konservatives Produktionsziel. 1024×1024 ist Research-/Patchbereich; der zentrale Terrainloader ist nicht allein der limitierende Faktor.

---

## 8. Logs / Diagnose

Logs werden rekursiv innerhalb der Serviceordner gesucht, nicht nur `Message.txt`/`Dbg.txt` im Root.

Unterstützt werden u.a. `.txt`, `.log`, `.err`, `.out`, `.trace`, `.dbg`; Erkennung erfolgt über Dateiname und typische Log-Unterordner.

Vollanalyse und Live-Monitor korrelieren:

- Service/PID-Wechsel
- Ports
- WM Readiness
- Zone Start/Stop
- Reconnects
- Parser-/Sessionfehler
- Windows Event Log
- PDB-Symbole

Die frühere Code-Kollision wurde in 0.3.4 korrigiert:

- `NG-ZONE-0020` = NPC-Pool / `Too many npc`
- erfolgreiche Runtime-Recovery = `NG-RUNTIME-0002`

`Closed ZN from` gilt nicht mehr pauschal als Capacity-Hardlimit.

---

## 9. Zone03 / WorldManager Startup

Historische Diagnose:

- Zone03 beendet sich beim normalen Startup und funktioniert nach manuellem späterem Start.
- WorldManager benötigt deutlich länger bis `SUCCESSED RUNNING SERVER` als der alte Startscript-Wait.
- PDB beweist Support für `NC_GUILD_GUILDWARSTATUS_REQ` (`dep=29 cmd=149`, `0x7495`).
- `SERVER_ID_UNKNOWN` + Parserfehler während Startup passen eher zu einer Session-/Readiness-Race als zu einem unbekannten Opcode.

Smart Start wartet deshalb auf:

1. WM RUNNING
2. internen WM-Zone-Port
3. aktuellen `SUCCESSED RUNNING SERVER` Marker
4. gestaffelten Zone-Start mit Stabilitätsprüfung

Zone5 ist nachweislich lauffähig; keinen fiktiven Zone0–Zone4-Hardcode annehmen.

---

## 10. Hardwarestrategie

Test-PC: hohe Pro-Kern-Leistung, aber nur 16 GB RAM.  
Zusätzlicher Server: Dell PowerEdge R720, derzeit 10-Core ~2,5 GHz und 128 GB RAM; CPU(s) austauschbar.

Für Fiesta ist die Single-Core/Mainthread-Leistung einzelner Zone-Prozesse entscheidend. Zweite CPU erhöht primär Parallelität, nicht die Geschwindigkeit eines einzelnen Zone-Mainthreads.

Aktuelle R720-Richtung: **2× Xeon E5-2667 v2** als Pro-Kern-orientiertes Profil, sofern Chassis/BIOS/Kühlung/TDP passen.

---

## 11. Nächste Reihenfolge

### Jetzt

1. UI auf das verbindliche Zielbild umbauen
2. Haupt-/Subtab-Navigation skalierbar machen
3. keinerlei bestehende Funktion verlieren
4. Windows-CI und reale Windows-Tests nutzen

### Danach

1. letzte 0.3.4 Testmessungen wieder aufnehmen
2. Anzeigeinkonsistenz WM 1500 vs. 3000 klären
3. Zone-Pool Live-Zähler
4. vollständige Handle-Rebase-Matrix
5. sichere Binary-Hook-Profile
6. adaptive Empfehlungen nach echter Pool-/CPU-/RAM-Auslastung
7. Spielfunktionen/DLL-Hook-Tab
8. OPTool

---

## 12. Build / CI

Zielplattform: Windows, .NET 8, WPF, win-x64.

GitHub Actions baut auf `windows-latest`.

---

## 13. Wichtigste Dateien im Quellstand

- `README.md`
- `docs/UI_TARGET.md`
- `docs/GIT_WORKFLOW.md`
- `docs/PROJECT_CONTEXT.md`
- `src/NextGen.Fiesta.ServerManager/MainWindow.xaml`
- `src/NextGen.Fiesta.ServerManager/ViewModels/MainViewModel.cs`
- `src/NextGen.Fiesta.ServerManager/Services/AdaptiveHookService.cs`
- `src/NextGen.Fiesta.ServerManager/Services/ZoneCapacityMonitor.cs`
- `src/NextGen.Fiesta.ServerManager/Services/PerformanceTuningAuditService.cs`

Dieses Dokument soll bei jedem größeren Meilenstein aktualisiert werden.
