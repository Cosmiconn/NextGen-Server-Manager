# Dell PowerEdge R720 – CPU-Empfehlung für Fiesta NA2016

Für Fiesta ist nicht nur die Gesamtzahl der Kerne wichtig. Zone.exe und WorldManager besitzen zentrale Mainthreads; deshalb zählt hoher Pro-Kern-Takt besonders stark.

## Bevorzugte Konfiguration

**2 × Intel Xeon E5-2667 v2**

- 8 Kerne / 16 Threads je CPU
- 3,30 GHz Basistakt
- bis 4,00 GHz Turbo
- 25 MB Cache
- 130 W TDP
- 2-Socket-fähig
- Dell führt den E5-2667 v2 für den PowerEdge R720/R720xd in der unterstützten E5-2600-v2-Liste.

Für einen normalen R720 ist dies der beste Kompromiss aus hoher Single-Core-Leistung und genügend Kernen für mehrere Zone-Prozesse. Zwei CPUs ergeben 16 physische Kerne / 32 Threads und zwei NUMA-Nodes.

## Alternativen

- **2 × E5-2690 v2** – 10C/20T je CPU, 3,0 GHz Basis, 3,6 GHz Turbo, 130 W. Mehr Parallelität, etwas weniger Pro-Kern-Takt als E5-2667 v2.
- **2 × E5-2697 v2** – 12C/24T je CPU, 2,7 GHz Basis, 3,5 GHz Turbo, 130 W. Maximale Kernzahl dieser sinnvollen R720-Auswahl, aber für einzelne Fiesta-Mainthreads weniger attraktiv.

Wenn die aktuelle CPU tatsächlich ein **E5-2670 v2** ist (10C/20T, 2,5 GHz Basis, 3,3 GHz Turbo), ist der E5-2667 v2 für Fiesta die deutlich interessantere vertikale Aufrüstung.

## Einbauhinweise

- Beide CPUs sollten dasselbe Modell/Stepping sein.
- Für CPU2 werden der zweite passende Kühlkörper und korrekt bestückte Speicherkanäle benötigt.
- 128 GB RAM möglichst symmetrisch auf beide CPU-Speicherkanäle verteilen.
- Bei 130-W-CPUs Dell-Kühlungs-/Netzteilvorgaben beachten; beim normalen R720 sind bis 130 W vorgesehen. Bei R720xd gelten je nach Chassis strengere Grenzen.
- BIOS/iDRAC vor dem Umbau aktualisieren. Wenn bereits ein E5-2600-v2-Prozessor läuft, ist die v2-Unterstützung grundsätzlich schon vorhanden.

## Scheduler-Ziel für Fiesta

Ein zweiter Sockel macht **eine einzelne Zone nicht doppelt so schnell**. Er ermöglicht mehr Zone-/DB-/Nebenprozesse parallel. Der Manager soll daher bei zwei NUMA-Nodes einzelne Zone-Prozesse möglichst node-lokal bewerten und später optional Affinitäts-/NUMA-Empfehlungen erzeugen.

Quellen: Dell PowerEdge R720/R720xd Technical Guide; Dell R720 Owner's Manual; Intel ARK E5-2667 v2 / E5-2690 v2 / E5-2697 v2.


## Hardware-aware CPU-Affinity im Manager

Der Manager kann Fiesta-Prozesse jetzt auf Basis der echten Windows-Topologie planen und pinnen:

- physische Cores werden über `GetLogicalProcessorInformationEx` erkannt;
- SMT-/Hyper-Threading-Geschwister bleiben als Einheit zusammen und werden nicht als zwei unabhängige physische Cores behandelt;
- WorldManager, Login, Character und Account erhalten nach Möglichkeit getrennte physische Core-Lanes;
- laufende Zone-Prozesse werden auf eigene, innerhalb eines NUMA-Nodes liegende Core-Masken verteilt;
- wenn nur eine Zone läuft und genügend Reserve vorhanden ist, erhält sie zwei physische Cores als Prozess-Affinity, damit Neben-/Netzwerkthreads nicht mit World/Login/Character um denselben physischen Core konkurrieren;
- AccountLog, GameLog und GamigoZR teilen sich bewusst die Hintergrund-Lane;
- bei zu wenigen physischen Cores wird die Teilung in der UI ausdrücklich angezeigt statt Exklusivität vorzutäuschen;
- Systeme mit mehr als 64 logischen CPUs bzw. mehreren Windows Processor Groups werden derzeit fail-closed nicht automatisch gepinnt.

Die Option **„Nach Service-/Prozess-Neustart automatisch erneut anwenden“** prüft die PIDs bei jedem Status-Refresh und setzt die geplante Affinity nur dann erneut, wenn Windows bzw. ein Service-Neustart sie verändert hat.

Wichtig: Dies ist Prozess-Affinity, kein Binary-Hook. Ziel ist reproduzierbare Isolation; ein einzelner Zone-Mainthread bleibt weiterhin durch seine eigene Single-Core-Leistung begrenzt.
