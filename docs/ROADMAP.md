# Roadmap

## 0.2 – Live Diagnostics ✅

- FileSystemWatcher-basierter Live-Tail mit Polling-Fallback
- Ereignis-Timeline über WM + alle Zonen
- Reconnect-/Crash-Loop Korrelation über Zeitfenster
- Windows Event Log / Application Error / SCM 7031/7034
- Prozess-RAM/CPU/Handle-/Thread-Zahlen
- Port -> PID Zuordnung
- WM-Ready-Marker aus dem aktuellen Log statt reinem Port-Wait
- Zone-Stabilitätsfenster beim Smart Start

## 0.3 – Reference Audit

- kompletter Dateibaum-Audit gegen ausgewählte Referenz
- „nur absichtlich modifizierte Dateien“ Allowlist
- SHA/Größe/PE-Version/PDB-GUID Vergleich
- Diff von ServerInfo/ZoneServerInfo/Startskripten
- Snapshot vor Änderungen und One-Click Restore für Textkonfigurationen

## 0.4 – Zone Wizard

- ZoneNN hinzufügen/klonen
- freie 3er-Portgruppe automatisch finden
- `PG_W00_ZNN` Einträge sicher in ServerInfo ergänzen
- ZoneServerInfo erzeugen
- Dienst installieren + Recovery setzen + Health-Test
- Zone-Service entfernen; optional Config-Zeilen entfernen (immer mit Backup)

## 0.5 – PDB Deep Analyzer

- Enum-/Struct-Extraktion aus NA2016-PDBs
- vollständiger `NC_*` Opcode-Katalog
- Handler-Mapping (`CParserZone::fc_*`, `WorldManagerSession::wms_*`)
- Crashadresse -> Symbol/Funktion
- Paketlängen gegen PDB-Structgrößen validieren
- Minidump/WER Fault Offset → Binary/PDB Symbol

## 1.0

- signierter Installer
- exportierbarer Diagnosebericht (JSON/HTML)
- stabile Reparaturbibliothek mit Safe/Config/Destructive Stufen
- optionaler Windows-Service/Tray-Watcher für unbeaufsichtigten Betrieb

## 0.3.2 – Vertical Scaling / Capacity Tuning ✅

- Live CPU-Core-/RAM-/Sessiondruck pro Kernkomponente
- PE32/LARGE_ADDRESS_AWARE-Prüfung
- PDB-Hinweise auf IOCP-Worker und zentrale Mainthreads
- Tuning-Kandidaten inkl. Rohspeicher-Delta für bekannte Objektpools
- klare Entscheidung: vertikal tunen vs. zusätzliche Zone
- keine automatischen Binary-Patches

## Nächster Performance-Schritt

- Live-Zähler für ShinePlayer/ShineMob/ShineNPC aus Prozessspeicher oder stabilen Runtime-Hooks
- reproduzierbarer Lastgenerator / Stresstestprofil
- P95/P99 Tick-/Packetlatenz und Queue-/IOCP-Stau erfassen
- WM-Sessionziel 3000/4500/6000 schrittweise qualifizieren
- Zone-Poolprofile (z. B. 2000 Player / 12000 Mobs / 512 NPCs) nur nach Lasttest freigeben
- sichere Hook-/Patch-Profile mit Hash-/Build-Gating, Backup und Rollback
