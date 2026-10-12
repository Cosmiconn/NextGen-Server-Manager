# Zone00 Disconnect 1669 – Einmaliger Laufzeitmesspunkt

**Status:** Werkzeug vorbereitet, kein Live-Capture ausgeführt. Ausschließlich auf einer separaten, nicht öffentlichen Testzone einsetzen. Die Original-EXE auf dem Datenträger und alle Serverlimits bleiben unverändert.

Weitere Evidenz: [ZONE1669_SYMBOL_ANALYSIS.md](ZONE1669_SYMBOL_ANALYSIS.md) und [SCALING_AND_PROVISIONING.md](SCALING_AND_PROVISIONING.md).

## Einfacher Weg: vorhandene Zone01 verwenden

Für den Rechner mit `C:\NextGenFiesta\Server\Zone01\Zone.exe` gibt es jetzt einen Ein-Befehl-Helfer:

    .\scripts\Start-Zone1669Zone01.ps1

Diesen Befehl **aus dem Repository-Hauptordner in PowerShell als Administrator** ausführen, während die isolierte Zone01 läuft. Das Skript:

1. findet `Zone01\Zone.exe` und sucht `Zone.pdb` in naheliegenden lokalen Ordnern;
2. prüft die originale EXE-SHA-256 und die **PDB-/PE-GUID/Age**; bei Abweichung wird **kein** Debugger-Befehl freigegeben;
3. erkennt die richtige laufende Zone01 anhand **ihres vollständigen Prozesspfades**, nicht bloß des Namens `Zone.exe`;
4. fragt ausdrücklich nach `TESTZONE01`. Nur eine **spielerfreie, nicht produktive** Zone bestätigen;
5. startet klassisches WinDbg x86 automatisch. Beim neuen Store-WinDbg zeigt es PID und einen kopierten Einfüge-Befehl an.

Wenn die originale `Zone.pdb` nicht automatisch gefunden wird, muss sie nur einmal hierhin kopiert werden:

    C:\NextGenZone1669Capture\Symbols\Zone.pdb

Danach **denselben** Startbefehl erneut ausführen. Die originale Zone.exe oder PDB wird nicht verändert. Wenn der SHA-256-Hash der Zone01 vom dokumentierten Original abweicht, **nicht** die Prüfung abschalten: dann liegt ein anderer Build vor, dessen Debug-Adressen erneut untersucht werden müssen.

Der Debugger legt beim ersten Treffer unter `C:\NextGenZone1669Capture\run-...\` die Datei `Zone1669_FirstHit_*.log` an. Diese Datei nach Durchsicht zur Diagnose bereitstellen. Die Originalserver-Verbindungen werden nicht absichtlich manipuliert; Debugger-Attach und Breakpoint können den Testprozess aber kurz pausieren.

Ohne einen angeschlossenen Testzone-Prozess kann die Messung **nicht** durchgeführt werden; der Befehl bereitet dann lediglich die Skripte vor. Mit `-PrepareOnly` kann man das bewusst so wählen.

## WinDbg-Skriptkorrektur (2026-10-12)

**Bekannter Fehler in älteren Skriptversionen:** Die Befehlsform `$>a<...`
fasst Skriptzeilen in **einen** Befehlsblock zusammen. Dadurch kann
`.sympath+` versehentlich `.reload`, `lmvm`, `bp`, `bl` und `g`
als weitere Symbolpfade interpretieren. Symptome sind
`Error: Execute .sympath(+) command attempts to access 'g'`,
`(no symbols)`, kein aktivierter Breakpoint und kein FirstHit-Log.

**Korrigiert:** Beide Aufrufe (Arm-Skript und Hit-Skript) verwenden
nun die **zeilenweise** Syntax `$<...`, nicht `$>a<...`.
Der Fehler bedeutet nicht, dass Zone01/PDB beschädigt wurden.

Wenn die alte Skriptdatei schon in WinDbg ausgeführt wurde:
- `g` aus dem früheren Versuch setzt die Zone lediglich fort, **ohne**
  den gewünschten Breakpoint. WinDbg über `Debug → Break` wieder anhalten
  (nur in einer bestätigten isolierten Testzone).
- Danach **aktuelle Skripte** aus GitHub holen und
  `.\scripts\Start-Zone1669Zone01.ps1 -PrepareOnly` ausführen,
  damit ein neuer `run-...\Zone1669_Arm.wds` generiert wird.
- Im bereits angehängten WinDbg den **neuen ausgegebenen**
  `$<C:\NextGenZone1669Capture\run-...\Zone1669_Arm.wds`-
  Befehl eingeben. **Keinen** zweiten Debugger an dieselbe PID anhängen.
- Im Ergebnis zuerst Symbole und `bl` prüfen, und erst nach
  plausibler Ausgabe fortsetzen. Der neue Arm-Skriptlauf führt
  dies bereits automatisch aus.

Alternativ zur sofortigen Fehlersuche auf der bestätigten Testzone,
wenn man die alte Hit-Skriptdatei beibehalten möchte:
```text
.sympath+ C:\NextGenZone1669Capture\Symbols
.reload /f Zone.exe
lmvm Zone
bp /1 Zone+0x0005AE5A "$<C:\NextGenZone1669Capture\run-20261012-080953-101\Zone1669_Hit.wds"
bl
g
```
Diese **sechs Befehle nacheinander im WinDbg-Command-Fenster**
ausführen, **nicht in PowerShell**. Vor `bp` die Symbolauflösung
mit `lmvm Zone` verifizieren und bei `(no symbols)` abbrechen.
Die manuelle Lösung benutzt dieselbe geprüfte EXE/PDB; keine Änderung
an `Zone.exe`.

Microsoft-Dokumentation: [WinDbg-Skriptbefehle](https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/-----------------------a---run-script-file-).

## Technische Grundlage

- Original Zone.exe (x86): SHA-256 db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5.
- PE/PDB GUID 339124ac-7257-466d-ac43-793bb53692ab, Age 16.
- RVA 0x0005AE51: Aufruf FieldMap::fm_Marking; RVA 0x0005AE56: test eax,eax; RVA 0x0005AE58: je Erfolgspfad; **RVA 0x0005AE5A: nur Fehlerzweig**, EAX ist hier noch der Fehlerwert.
- RVA 0x0005AE66: push 0x685 (= 1669), danach virtueller Disconnect.
- ESI hält den ShineMobileObject-Zeiger. [ESI+0x66] ist ein Zeiger auf zwei DWORD-Koordinaten X,Y; [ESI+0x7A] ist der FieldMap-Zeiger; [FieldMap+0x18] bzw. +0x1C sind X-/Y-Grenzwerte.
- PDB-Enum: 0 FM_MARKSUCCESS; 1 FM_MAPNOTEXIST; 2 FM_OTHERMAP; 3 FM_BLOCKED.

Der Debugger setzt **nur einen One-Shot-Breakpoint** am Fehlerzweig (Zone+0x0005AE5A), nicht auf dem häufig ausgeführten fm_Marking-Einstieg. Ein Debugger verändert dabei zwar vorübergehend den laufenden Prozessspeicher zum Setzen des Breakpoints, **nicht** die Zone.exe-Datei. Debugger-Pausen können Anmeldungen beeinflussen.

## Voraussetzungen

1. Isolierte Testzone, WinDbg **x86** (Debugging Tools for Windows), administrative Debuggerrechte. Kein Attach an den Server des 1600er-Kapazitätsbenchmarks.
2. Exakt die oben genannte original Zone.exe und passende Zone.pdb; nicht eine gehookte/gepatchte Kopie. Das Vorbereitungsskript lehnt SHA-, PE-RSDS- **und PDB-Info-Stream-GUID/Age-Abweichungen** ab.
3. PDB- und Capture-Verzeichnis ohne Leerzeichen oder problematische Sonderzeichen (Beispiel: C:\NextGenSymbols und C:\NextGenZone1669Capture).
4. Bei WinDbg-Attach anhand von **lmvm Zone** verifizieren, dass der tatsächlich geladene Testprozess zu der geprüften Datei gehört und die passenden PDB-Symbole geladen wurden. Sonst nicht fortfahren.
5. Der Trace enthält möglicherweise private Prozess- und Charakterdetails. Nur nach Durchsicht weitergeben.

## PowerShell – Vorbereitung

Aus dem Root von NextGen-Server-Manager in PowerShell:

    .\scripts\New-Zone1669OneShotCapture.ps1 -TemplateSelfTest

    .\scripts\New-Zone1669OneShotCapture.ps1 -ZoneExePath 'C:\NextGenFiestaServer\Zone\Zone.exe' -ZonePdbPath 'C:\NextGenSymbols\Zone.pdb' -OutputDirectory 'C:\NextGenZone1669Capture'

Die Pfade sind **Beispiele** und an den tatsächlichen separaten Testserver anzupassen. Der Vorgang prüft die Zone.exe-Datei **und die echte Zone.pdb-MSF-Info-Signatur** und erzeugt erst danach in einem neuen Zeitstempel-Unterordner:

- Zone1669_Arm.wds – ein einmaliger Breakpoint auf der 1669-Fehler-Verzweigung, dann fortsetzen.
- Zone1669_Hit.wds – Register, Rückgabewert, Koordinaten, FieldMap-Grenzen, Referenzwerte und Aufrufstack aufnehmen, dann fortsetzen.
- Zone1669_FirstHit_*.log – entsteht **erst beim Treffer**, benannt mit PID/Zeitstempel.

**Das Vorbereitungsskript verbindet sich nicht selbst mit Zone.exe.** Es startet, patcht oder stoppt keinen Prozess.

## Ausführung auf der separaten Testzone

1. Die geprüfte Original-Zone.exe im reproduzierbaren Testumfeld starten.
2. WinDbg **x86** als Administrator öffnen: File → Attach to a Process → genau diesen isolierten Zone.exe-Prozess auswählen.
3. lmvm Zone im Debugger kontrollieren (Pfad, Symbole, PDB). Bei Abweichung hier stoppen.
4. Den vom Vorbereitungsskript ausgegebenen Befehl in das Debugger-Command-Fenster kopieren. Beispiel:

    $$>a<C:\NextGenZone1669Capture\run-YYYYMMDD-HHMMSS-fff\Zone1669_Arm.wds

5. Der Script-Block setzt **bp /1 Zone+0x0005AE5A** und setzt Zone.exe mit g fort. Reproduktion nur mit kontrolliertem Testverkehr versuchen.
6. Beim ersten Treffer werden Fehler-Rückgabecode, die konkreten X/Y-Werte, Map-Grenzen, Rohregister und der symbolisierte Aufrufstack in die Logdatei geschrieben. Danach wird der Breakpoint entfernt und der Testprozess mit gc automatisch fortgesetzt.
7. Test nach der Messung ordentlich beenden und die Logdatei sichern. Das erneute Beenden des Debuggers bewusst prüfen, damit der Testprozess nicht unbeabsichtigt beendet wird.

## Auswertung / Beweisgrenze

Im Trefferlog stehen MARK_RC (EAX), CANDIDATE_X/Y, MAP_X_BOUND/Y_BOUND, Original-DWORDs, lmvm Zone und kv 20. Die Stack-Lokale [EBP-0x24]/[EBP-0x20] sind lediglich Referenzdaten – noch nicht sicher die alte Spielerposition.

- Wenn EAX = 3 und X > X_BOUND oder Y > Y_BOUND, ist das ein konkreter, reproduzierter FieldMap-Grenzfehler für diese Version.
- Ein anderer EAX-/Koordinaten-Befund widerlegt **nicht** andere 1669-Abbrüche; er belegt zunächst nur diesen einen Treffer.
- Der Callstack kann smo_SkillBash_Blast und so_Routine zuordnen. **Skill-ID, Angreifer, Ziel, Mapname** sind dadurch noch nicht automatisch bekannt. Erst aus einem echten Treffer kann ein nachfolgender, gezielter Messpunkt entwickelt werden.
- Der CoordRepairLog-Eintrag TSStart/... ist nicht automatisch der fehlerhafte Koordinatenwert.
- Kein Log bedeutet, dass die Fehlerstelle in diesem Lauf nicht getroffen wurde (oder die Debugger-Konfiguration fehlerhaft war); kein Beleg für einen behobenen Fehler.

Keine Binary-Hooks/Hardlimit-Anhebung, kein Ignorieren des Disconnects und kein Umgehen des strengen SH6/2-, CH6/3- oder ShinePlayer-PASS-Kriteriums.

Microsoft-Dokumentation: [One-shot Breakpoint /1](https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/bp--bu--bm--set-breakpoint-) und [Debugger-Script-Dateien](https://learn.microsoft.com/en-us/windows-hardware/drivers/debuggercmds/using-script-files).
