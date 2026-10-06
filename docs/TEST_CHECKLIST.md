# Test-Checkliste für den ersten Windows-Build

## A. Build

- [ ] `scripts\Check-Prerequisites.ps1` zeigt .NET 8 SDK = OK
- [ ] `scripts\Build.ps1` endet ohne Fehler
- [ ] `NextGen.Fiesta.ServerManager.exe` liegt im Publish-Ordner
- [ ] Anwendung startet per UAC als Administrator

## B. Read-only Diagnose

- [ ] NA2016 Server Root wird erkannt
- [ ] Account / AccountLog / Login / Character / GameLog / WorldManager werden erkannt
- [ ] Zone00 bis zur höchsten vorhandenen Zone werden dynamisch erkannt
- [ ] Client-/Internal-/OPTool-Ports entsprechen `ServerInfo.txt`
- [ ] Prozess-PID stimmt mit Windows Task Manager überein
- [ ] Port PID stimmt bei laufenden Diensten mit Prozess-PID überein
- [ ] CPU/RAM/Handles werden nach zwei Refresh-Zyklen angezeigt

## C. Live Monitor

- [ ] `Live Timeline` zeigt neue Message-/Dbg-Zeilen
- [ ] neue `Msg_*.txt` Datei nach Logrotation wird automatisch aufgenommen
- [ ] `RECONNECT` erscheint mindestens als Warning
- [ ] `dep=29 cmd=149 length=2` wird als Opcode `0x7495` ergänzt
- [ ] Dienst STOPPED/RUNNING erzeugt einen Runtime-Transition-Eintrag

## D. Zone03 / Smart Start

1. Alle Fiesta-Dienste sauber stoppen.
2. `Smart Start` ausführen.
3. Im Activity-Log prüfen:
   - WM Port wird erkannt
   - `SUCCESSED RUNNING SERVER` wird erkannt oder Fallback-Stabilitätsfenster wird verwendet
   - Zonen starten sequenziell
   - jede Zone muss mindestens 3 Sekunden stabil sein
4. Zone03 mindestens 2 Minuten beobachten.
5. Erwartung: kein manuelles Nachstarten nötig.

Wenn Zone03 trotzdem stirbt:

- sofort `Vollanalyse`
- `Diagnosebericht exportieren`
- Windows Application/System Event Einträge und Runtime-Timeline bleiben im Bericht erhalten

## E. Service-Aktionen

Nur an einem Test-/Backup-Server durchführen.

- [ ] Stop einer Zone
- [ ] Start derselben Zone
- [ ] Restart
- [ ] Recovery setzen
- [ ] Dienst löschen: Dateien bleiben vorhanden
- [ ] Neu installieren: Service wird wieder registriert

## F. PDB

Wenn LLVM installiert ist:

- [ ] `llvm-pdbutil.exe --version` funktioniert
- [ ] `PDBs indexieren` erzeugt `.nextgen-cache\pdb\*.symbols.txt`
- [ ] Suche nach `CParserZone`
- [ ] Suche nach `GUILDWARSTATUS`
- [ ] Suche nach `WorldManagerSession`

## 0.3.1 Zone Capacity / Scaling

- [ ] `Zone Auslastung / Scaling` zeigt jede ZoneNN genau einmal.
- [ ] Bei laufender Zone wird die Zahl etablierter Client-Sessions aktualisiert.
- [ ] CPU zeigt nach mindestens zwei Refresh-Zyklen einen Core-äquivalenten Wert.
- [ ] Private Memory / Working Set werden angezeigt.
- [ ] Konfigurierte Maps stimmen grob mit der Field.txt-Zuordnung überein.
- [ ] WorldManager zeigt Client-Sessions und Zone-/S2S-Sessions getrennt.
- [ ] `Neue Zone planen` verändert keine Dateien und keinen Dienst.
- [ ] Stock Zone00–04 ergibt als nächstes Zone05 und normalerweise Ports 9035/9036/9037, sofern diese frei sind.
- [ ] Vor Provisionierung ist ein Backup-Pfad unter `.nextgen-backups/provisioning` vorgesehen.
- [ ] `Zone jetzt anlegen` ist ohne Administratorrechte deaktiviert.
- [ ] Nach Provisionierung existieren ZoneNN, ZoneServerInfo, drei ServerInfo-Zeilen und Windows-Dienst `_ZoneN`.
- [ ] Firewall enthält nur die eingehende TCP-Regel für den Client-Port; interner/OPTool-Port wurden nicht geöffnet.
- [ ] Neue Zone bleibt nach Provisionierung gestoppt und erhält keine automatische Field.txt-Zuordnung.


## 0.3.2 Vertical Scaling / Performance

- [ ] Tab `Performance / Vertical Scaling` öffnet ohne Binding-Fehler.
- [ ] Zone/WorldManager zeigen `PE32 + LAA`.
- [ ] Bei vorhandenen PDBs wird `IOCP + Mainthread` für Zone/WM erkannt.
- [ ] CPU-Core, Private/Virtual Memory, Threads, Handles und Sessions aktualisieren sich.
- [ ] Bei <50 Sessions bzw. <5% Core-CPU bleibt das lineare CPU-Modell auf `lernt / zu wenig Last`.
- [ ] Bei >=90% Core-CPU lautet die Empfehlung horizontal skalieren statt Pools erhöhen.
- [ ] Tuning-Kandidaten zeigen plausible Rohspeicher-Deltas.
- [ ] Diagnosebericht enthält `performanceTuning`.
