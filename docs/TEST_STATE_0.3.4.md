# Windows-Teststand 0.3.4 – vor UI-Redesign

Stand: 2026-10-05

Dieses Dokument friert den letzten realen Windows-Teststand ein, damit nach der UI-Umstellung exakt an derselben technischen Stelle weitergearbeitet werden kann.

## Testsystem

- AMD Ryzen 5 3600, 6 Kerne / 12 logische CPUs
- 1 NUMA-Node
- ungefähr 3,59 GHz nominal laut Manager
- ungefähr 15,9 GB RAM

Zusätzlich verfügbar für spätere Lasttests: Dell PowerEdge R720, derzeit 10-Core ~2,5 GHz und 128 GB RAM; CPU-Ausbau auf zwei CPUs möglich.

## Zone Capacity – letzter sichtbarer Stand

- 5/6 Zonen laufen; Zone03 ist gestoppt.
- Zone00: ungefähr 75–77 % eines Cores, ~2,1 GB Private RAM, 49/256 Maps/BlockInfo, echter Mob-Limit-Hinweis im Log.
- Zone01: ungefähr 73–77 % eines Cores, ~1,9 GB Private RAM.
- Zone02: ungefähr 72–76 % eines Cores, ~2,1 GB Private RAM.
- Zone04/Zone05: sehr geringe CPU-Grundlast.
- WorldManager: ungefähr 1 % Core, ~478 MB Private RAM, 20/100 Zone-Sessions.

Wichtig: Gesamt-CPU ist kein ausreichender Skalierungsindikator; die belasteten Zone-Mainthreads liegen bereits deutlich höher.

## Performance / Vertical Scaling

Der Manager erkennt den Test-PC als Single-Core/Mainthread-limitiert mit Parallelreserve. Für weniger Zonen ist höhere Pro-Kern-Leistung wichtiger als zusätzliche langsame Kerne.

Die bereits dokumentierte R720-Richtung bleibt deshalb: 2× Xeon E5-2667 v2 als Pro-Kern-orientiertes Profil, sofern Chassis/BIOS/Kühlung/TDP passen.

## Adaptive Hooks – letzter sichtbarer Stand

Profil **Ausgewogen** wurde angewendet.

- WM Client Sessions: 3000 / 3000
- WM S2S: 150 / 150
- Zone Listener: 1500 / 1500
- ShinePlayer Ziel 2000: **BLOCKIERT**
- ShineMob Ziel 12000: **BLOCKIERT**
- ShineNPC Ziel 512: **BLOCKIERT**

Die Zone-Binary-Hooks bleiben gesperrt, bis die 16-Bit-Handle-Rebase-Matrix vollständig verifiziert ist.

## Offener Konsistenzpunkt nach UI-Redesign

In Capacity-/anderen Ansichten ist teilweise noch `WM Clients 0/1500` sichtbar, während Adaptive Hooks `3000/3000` meldet. Nach dem UI-Umbau muss geklärt werden, ob dies ein Neustart-/Hard-Pool-Effekt, eine unterschiedliche Datenquelle oder eine ViewModel-Anzeigelücke ist. Nicht nur kosmetisch ändern.

## Nächster technischer Block nach UI-Abnahme

1. WM 1500-vs-3000 Datenquelle klären.
2. Live-Poolzähler für Zone/WM ergänzen.
3. vollständige Handle-Rebase-Matrix fertigstellen.
4. hashgebundene atomare Pool-Hooks + Rollback.
5. Lasttests auf Test-PC und R720 vergleichen.
6. Spielfunktionen/DLL-Hook-Modul und später OPTool als eigene Feature-Branches.
