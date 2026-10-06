# NA2016 Performance / Vertical Scaling

Ziel: Mehr Last pro bestehender Komponente tragen, bevor weitere Zone-Prozesse angelegt werden.

## Architektur, die für das Tuning zählt

Die untersuchten `Zone.exe` und `WorldManager.exe` sind 32-Bit-PEs mit gesetztem `LARGE_ADDRESS_AWARE`-Flag. Auf 64-Bit-Windows ist damit mehr Adressraum als bei einem normalen 32-Bit-Prozess möglich. Der Manager verwendet trotzdem ein konservatives Betriebsbudget von ca. 3 GiB Private Bytes, weil Fragmentierung, Stacks, DLLs und temporäre Allokationen Reserve benötigen.

Beide Binaries importieren `CreateIoCompletionPort`, `GetQueuedCompletionStatus` und `CreateThread`. Die PDBs enthalten `CIOCP::WorkThread`. Netzwerk-I/O ist daher nicht rein single-threaded.

Gleichzeitig enthalten die PDBs zentrale Ausführungspfade wie:

- `ZoneServer::zs_mainthreadfunction`
- `ZoneServer::zs_ServiceThreadFunction`
- `WorldManagerServer::MainThread`
- `WorldManagerServer::Start_MainThread`

Daraus folgt für die Praxis: höhere Session-/Objektpools können Speichergrenzen verschieben, aber eine Zone kann trotzdem an ihrer zentralen Gameplay-/Tick-Schleife einen CPU-Kern sättigen. In diesem Fall ist horizontale Verteilung auf mehrere Zone-Prozesse wirksamer als ein noch höheres Poollimit.

## Aktuelle bekannte Zone-Rohpools

Die bereits binär verifizierten Objektpools reservieren zusammen rund **441 MiB Rohspeicher** (ohne Container-Overhead, Heap-Metadaten, Skripte, Maps, Netzwerkpuffer usw.).

Wichtige Anteile:

- ShinePlayer 1500 × 0x2C058: ~257.9 MiB
- ShineMob 8000 × 0x2568: ~73.1 MiB
- ShineNPC 256 × 0x256C: ~2.34 MiB
- ShineMiniHouse 1000 × 0xD100: ~51.0 MiB
- übrige bekannte Pools: ~56.9 MiB

Ein moderates Research-Profil mit ShinePlayer 2000, ShineMob 12000 und ShineNPC 512 erhöht die bekannten Rohpools um rund **124.8 MiB** auf rund **566 MiB**. Speicher allein wäre damit noch nicht der wahrscheinlichste erste Flaschenhals; CPU/Tick/Visibility müssen unter realer Last gemessen werden.

## WorldManager

Der 2016-Code besitzt einen Sessionmanager mit `m_MaxSessions`, `m_NumSessions`, einer dynamischen Sessionliste und einem Client-Sessionarray. Der bekannte `InitSessions(maxSessions)`-Pfad sowie `g_UserLimit` sprechen dafür, dass der WM-Clientpool grundsätzlich vertikal konfigurier-/hookbar ist. Trotzdem muss jede Erhöhung gegen CPU, Speicher und globale Party/Guild/KQ-Strukturen getestet werden.

Der Manager schlägt deshalb 3000 WM-Clients nur als **Lasttest-Ziel**, nicht als garantierte Produktionsfreigabe, vor.

## Entscheidungsregel

- CPU-Core < 70 % und Private Bytes deutlich unter Budget: vertikales Tuning sinnvoll testen.
- CPU-Core 70–89 %: nur moderate Erhöhung, P95/P99-Tick-/Packetlatenz beobachten.
- CPU-Core >= 90 %: keine blinde Poolerhöhung; Mainthread ist wahrscheinlich der Engpass.
- Private Bytes >= 90 % des konservativen 32-Bit-Budgets: keine weitere Poolerhöhung.
- harte `full`/`too many`-Logs bei niedriger CPU/RAM-Last: Poollimit ist der wahrscheinlichste Engpass und ein guter Patch-/Hook-Kandidat.

## Was 0.3.2 bewusst noch nicht tut

- keine EXE-Patches
- keine DLL-Hooks automatisch installieren
- keine Prozesspriorität oder CPU-Affinität erzwingen
- keine WM-/Zone-Limits ohne Backup/Lasttest verändern

Der nächste sichere Schritt ist ein reproduzierbarer Stresstest mit echten oder simulierten Sessions und Messung von CPU-Core, Private Bytes, Tick-/Packetlatenz und Fehlerraten vor/nach jeder einzelnen Änderung.
