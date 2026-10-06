# 0.3.4 – Hardware-aware scaling + capacity correctness

- Host-Hardware-Profil: CPU-Modell, nominaler Takt, logische CPUs, NUMA-Nodes, physischer RAM und Host-CPU.
- Performance-Tab zeigt automatisch, ob die Maschine eher Mainthread-/Single-Core-, RAM- oder parallelitätslimitiert ist.
- Zweiter Sockel wird korrekt als horizontale Parallelreserve behandelt, nicht als Beschleunigung eines einzelnen Zone-Mainthreads.
- Capacity-Falschpositiv behoben: Runtime-Recovery verwendete versehentlich denselben Fehlercode wie `Too many npc`.
- `Closed ZN from` wird nicht mehr als Pool-/Map-Hardlimit behandelt.
- Historische Logdateien können nicht mehr allein durch einen fehlenden Zeitstempel einen frischen Hard-Limit-Alarm erzeugen; für dateibasierte Capacity-Ereignisse wird zusätzlich die letzte Dateischreibzeit geprüft.
- Zone-Pool-Reverse-Engineering erweitert: zentraler Allokationsblock und 13 Count/Stride-Paare des Baseline-Builds dokumentiert.
- Dell-R720-CPU-Leitfaden hinzugefügt; für Fiesta wird 2× E5-2667 v2 als bevorzugte R720-Konfiguration dokumentiert.
