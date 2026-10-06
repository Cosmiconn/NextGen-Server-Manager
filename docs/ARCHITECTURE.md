# Architektur

## Schichten

- **FiestaTopologyScanner**: erkennt Dienste, Zonen, Config/PDB-Pfade und ordnet Ports aus `ServerInfo.txt` zu.
- **WindowsServiceManager**: liest und steuert SCM-Dienste ausschließlich über Windows `sc.exe`.
- **NetworkInspector**: prüft TCP-Readiness und kann Listener-PIDs ermitteln.
- **ConfigurationAuditor**: prüft `MY_SERVER`, Zone-ID, fehlende EXE/Config und doppelte Ports.
- **ReferenceAuditService**: SHA-256 Vergleich mit der geprüften NA2016-Basis; alle Zonen werden gegen dieselbe Zone.exe-Referenz verglichen.
- **LogAnalyzer**: regelbasierte Diagnose plus Fiesta Opcode-Berechnung aus Department/Command.
- **PdbSymbolService**: optionaler Symbolcache über `llvm-pdbutil dump -types -symbols`.
- **SmartOrchestrator**: abhängiger Serverstart; wartet auf WM und jede Zone statt fixer Sleep-Zeiten.
- **ZoneLifecycleService**: native Service-Installation/Reinstallation entsprechend dem Stock-NA2016 Installskript.

## Keine Binary-Patches

Das MVP verändert keine EXE/PDB. Binary-Abweichungen werden nur gemeldet. Patches gehören später in einen gesonderten, explizit bestätigten Workflow mit Backup und exakter Build-Erkennung.
