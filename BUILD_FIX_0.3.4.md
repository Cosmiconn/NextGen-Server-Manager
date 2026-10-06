# Build / Runtime notes 0.3.4

## Behoben

- Diagnostic-Code-Kollision `NG-ZONE-0020`: Recovery-Korrelation verwendet jetzt `NG-RUNTIME-0002`; `NG-ZONE-0020` bleibt ausschließlich `Too many npc`.
- `Closed ZN from` wird nicht mehr als Capacity-Hardlimit gewertet.
- Historische timestamp-lose Logzeilen können nur dann als frischer Pool-/Map-Alarm gelten, wenn die Quell-Logdatei selbst innerhalb des 5-Minuten-Fensters geschrieben wurde.
- Hardware-Advisor hinzugefügt (Registry CPU/System-ID + NUMA + Systemressourcen).

## Prüfung in dieser Umgebung

- `App.xaml` und `MainWindow.xaml` als XML erfolgreich geparst.
- Projektversion auf 0.3.4 gesetzt.
- Statische Source-/Dateistruktur geprüft.
- Keine Windows-WPF-Kompilierung in dieser Linux-Umgebung möglich, da kein .NET SDK vorhanden ist. Bitte unter Windows `scripts\\Build.ps1` ausführen.
