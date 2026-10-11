# Zone00 Disconnect 1669 – Einmaliger Laufzeitmesspunkt

**Status:** Werkzeug vorbereitet, kein Live-Capture ausgeführt. Ausschließlich auf einer separaten, nicht öffentlichen Testzone einsetzen. Die Original-EXE auf dem Datenträger und alle Serverlimits bleiben unverändert.

Weitere Evidenz: [ZONE1669_SYMBOL_ANALYSIS.md](ZONE1669_SYMBOL_ANALYSIS.md) und [SCALING_AND_PROVISIONING.md](SCALING_AND_PROVISIONING.md).

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
2. Exakt die oben genannte original Zone.exe und passende Zone.pdb; nicht eine gehookte/gepatchte Kopie. Das Vorbereitungsskript lehnt SHA-/PE-RSDS-Abweichungen ab.
3. PDB- und Capture-Verzeichnis ohne Leerzeichen oder problematische Sonderzeichen (Beispiel: C:\NextGenSymbols und C:\NextGenZone1669Capture).
4. Bei WinDbg-Attach anhand von **lmvm Zone** verifizieren, dass der tatsächlich geladene Testprozess zu der geprüften Datei gehört und die passenden PDB-Symbole geladen wurden. Sonst nicht fortfahren.
5. Der Trace enthält möglicherweise private Prozess- und Charakterdetails. Nur nach Durchsicht weitergeben.

## PowerShell – Vorbereitung

Aus dem Root von NextGen-Server-Manager in PowerShell:

    .\scripts\New-Zone1669OneShotCapture.ps1 -TemplateSelfTest

    .\scripts\New-Zone1669OneShotCapture.ps1 -ZoneExePath 'C:\NextGenFiestaServer\Zone\Zone.exe' -ZonePdbPath 'C:\NextGenSymbols\Zone.pdb' -OutputDirectory 'C:\NextGenZone1669Capture'

Die Pfade sind **Beispiele** und an den tatsächlichen separaten Testserver anzupassen. Der Vorgang prüft die Zone.exe-Datei und erzeugt in einem neuen Zeitstempel-Unterordner:

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
