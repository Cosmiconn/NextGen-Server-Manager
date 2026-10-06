# 0.3.2 – Vertical Scaling & Performance Audit

Ziel: Vor dem Anlegen weiterer Zonen prüfen, wie weit sich die vorhandenen 32-Bit-Komponenten sicher vertikal skalieren lassen.

## Verifizierte Architekturhinweise

- `Zone.exe` und `WorldManager.exe` sind PE32 und LARGE_ADDRESS_AWARE.
- Die PDBs enthalten IOCP-Netzwerkworker (`CIOCP::WorkThread`).
- Gleichzeitig existieren zentrale Mainthread-Funktionen (`ZoneServer::zs_mainthreadfunction`, `WorldManagerServer::MainThread`).
- Deshalb kann Netzwerk-I/O parallel skalieren, während Gameplay-/World-Logik später an einem Kern limitieren kann.

## Tuning-Reihenfolge

1. Reale CPU-Core-/RAM-/Session-Last messen.
2. Wenn CPU-Core deutlich unter 70 % und RAM Reserve hat: Session-/Objektpools moderat erhöhen.
3. Wenn CPU-Core 85–100 % erreicht: keine blinde Pool-Erhöhung; Last auf weitere Zonen verteilen.
4. 32-Bit-/LAA-Prozesse konservativ unter ~3 GiB Private Bytes halten, obwohl der theoretisch adressierbare Raum auf 64-Bit-Windows größer sein kann. Fragmentierung und zusätzliche Allokationen benötigen Reserve.
5. Erst nach reproduzierbaren Lasttests höhere Produktionslimits übernehmen.

## Beispiel-Zielgrößen im Audit

- WM Client Sessions: 1500 -> 3000 (Config/Sessionmanager, Lasttest erforderlich)
- ShinePlayer: 1500 -> 2000 (+~86 MiB Rohpool; Binary/Hook)
- ShineMob: 8000 -> 12000 (+~36.5 MiB Rohpool; Binary/Hook)
- ShineNPC: 256 -> 512 (+~2.34 MiB Rohpool; Binary/Hook)
- MapBlockInformation: 256 -> 384 (Binary/Hook und abhängige Container prüfen)
- MapCluster: 512 -> 768 (Binary/Hook und abhängige Indizes prüfen)

Die Zielgrößen sind bewusst **Planungswerte**, keine automatisch freigegebenen Produktionslimits.
