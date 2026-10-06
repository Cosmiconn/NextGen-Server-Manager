# NA2016 – verifizierte Limits / Kapazitäten

Stand: 0.2.9. Die Werte stammen aus den bereitgestellten NA2016-Binaries/PDBs, der ServerInfo-Konfiguration und der serverseitigen BlockInfo-Struktur. Werte werden nur als „hart“ geführt, wenn sie im Binary/Format konkret belegt sind.

## Socket-/Session-Limits

| Dienst/Listener | nBackLog | nMaxAccept | Bedeutung |
|---|---:|---:|---|
| WorldManager Client | 100 | 1500 | Client-/WM-Sessions |
| WorldManager Zone | 100 | 100 | interne Zone→WM-Socket-Sessions; **nicht** Zone-IDs |
| WorldManager OPTool | 100 | 30 | OPTool |
| jede Zone Client | 100 | 1500 | Client-Sessions |
| jede Zone intern | 1 | 30 | interne Zone-Sessions |
| jede Zone OPTool | 100 | 30 | OPTool |

## Zone.exe – ShineObjectManager-Pools

| Pool | Count | Objekt-Stride | rohe Pool-Nutzlast ca. |
|---|---:|---:|---:|
| ShinePlayer | **1,500** | 180,312 B | 257.9 MiB |
| ShineMob | **8,000** | 9,576 B | 73.1 MiB |
| ShineNPC | **256** | 9,580 B | 2.3 MiB |
| ShineBandit | 2,048 | 9,836 B | 19.2 MiB |
| ShinePet | 1,500 | 9,684 B | 13.9 MiB |
| ShineMover | 1,000 | 8,236 B | 7.9 MiB |
| ShineServant | 500 | 9,624 B | 4.6 MiB |
| ShineMiniHouse | 1,000 | 53,504 B | 51.0 MiB |
| ShineMagicField | 250 | 456 B | 0.1 MiB |
| ShineDoor | 1,000 | 7,956 B | 7.6 MiB |
| ShineEffectObject | 1,000 | 467 B | 0.4 MiB |
| ShineDropItem | 3,000 | 651 B | 1.9 MiB |
| ShineAxialFlag | 3,584 | 392 B | 1.3 MiB |

Die Summe dieser reinen Objekt-Strides liegt bereits bei ungefähr **441 MiB**. Das ist nicht identisch mit dem gesamten reservierten/committeten Speicher, zeigt aber, warum die 32-Bit-Zone trotz Large-Address-Aware praktisch lange vor theoretischen Integer-Grenzen an Speicher-/Fragmentierungsgrenzen kommen kann.

## Zone.exe – weitere harte Puffer

| Limit | Wert | Evidenz / Einordnung |
|---|---:|---|
| MapCluster Registry | **512** | `cmp ..., 0x200` vor `Too many mapcluster` |
| MapBlockInformation | **256** | `cmp ..., 0x100` vor `Too many block info` |
| BlockDistributeManager | **64** | `cmp ..., 0x40` vor `Too many BlockDistribute` |
| KingdomQuest Entrance | **100** | Vergleich `0x64` vor `Too many entrance` |
| MAXQUEST-Pfad | **3000** | `0x0BB8` vor `Too Many Quest - MAXQUEST`; kontextbezogen |

## WorldManager.exe

| Limit | Wert | Einordnung |
|---|---:|---|
| Kingdom-Quest-Buffer | **300** | `0x12C` |
| Guild Count | **16,384** | `0x4000` |
| Friend-List-Paketpfad | 100 | `0x64` |
| Chat-Restrict-Paketpfad | 100 | `0x64` |

## Einzelne Map / SHBD

Die serverseitige `.shbd`-Datei beginnt mit zwei Little-Endian `Int32`-Werten:

- `XSize` = Bytes pro Kollisionszeile
- `YSize` = Zeilen
- danach exakt `XSize × YSize` Bytes Daten
- ein Byte enthält 8 Kollisionsbits → Gridbreite = `XSize × 8`

Die Zone-MapBlock-Logik leitet daraus ab:

- Blockbreite = `8 × XSize`
- Blockhöhe = `YSize`
- serverseitige X-Ausdehnung = `50 × XSize`
- serverseitige Y-Ausdehnung = `6.25 × YSize`

Im gelieferten NA2016-Bestand sind `Adl.shbd` und `AdlVal01.shbd` die größten gefundenen BlockInfo-Dateien:

- Header: `X=950`, `Y=7600`
- Kollisionsgrid: **7600 × 7600** Zellen
- Payload: **7,220,000 Bytes** (+ 8 B Header)
- daraus berechnete serverseitige Blockfläche: **47,500 × 47,500 Koordinateneinheiten**

`Field.txt` führt `Adl` gleichzeitig mit `xsize=950`, `ysize=475`. Das zeigt, dass Field-Metadaten und die SHBD-Blockfläche nicht pauschal als dieselbe Dimension behandelt werden dürfen. Für eine spielbare Karten-Maximalgröße müssen zusätzlich Client-Terrain-/Renderformate berücksichtigt werden.

### Was ist das absolute Maximum?

Im untersuchten `MapBlockInformation::mbi_Load` der Zone wurde **keine feste 512/1024/2048-Dimensionsprüfung** gefunden. X und Y werden als 32-Bit-Werte gelesen, multipliziert und die Payload dynamisch alloziert. Damit ist serverseitig kein kleiner fester Kartenbreiten-Hardcode belegt.

**Sicher belegt ist daher:** NA2016 kann mindestens die vorhandene 7600×7600-SHBD-Kollisionsfläche bzw. 47,500×47,500 serverseitige Blockfläche laden. Eine größere Karte als „client-sicher“ zu deklarieren wäre ohne Analyse der Client-Terrainformate/Fiesta.bin nicht belastbar.

## 0.3.0 – Client-Terrain-Sicherheitsgrenze

Die konkrete NA2016 `Fiesta.bin` wurde zusätzlich statisch geprüft. Der zentrale HTD/HeightMap-Loader verwendet dynamische 32-Bit-Dimensionen und dynamische Allokationen; ein einfacher Terrain-Hardcap bei 512/1024/2048 wurde im Kernpfad nicht gefunden.

Für den **unveränderten NA2016-Client** wird trotzdem **512×512 Terrain-Quads** als konservatives produktives Ziel geführt. Gründe:

- 512×50 = 25,600 Weltunits und damit unter einer signed-16-bit-Seite,
- Community-Berichte nennen 2016 bei 1024×1024 unter Inhalt/Last als massiv problematisch,
- separate 1024-Support-Arbeiten mussten 16-Bit-Koordinaten-/NPC-/Packetpfade auf größere Typen umstellen.

Die daraus abgeleitete 655/656-Grenze (`32767 / 50`) ist eine **Inference für mögliche signed-16-bit-Pfade**, kein universeller Hardcap. Details stehen in `CLIENT_MAP_LIMITS.md`.

## 0.3.1 Live-Kapazitätsbezug

Die harten Werte werden jetzt im Tab `Zone Auslastung / Scaling` mit messbaren Laufzeitwerten kombiniert. Direkt messbar sind etablierte Client-Sessions, CPU, Prozessspeicher und konfigurierte Maps. Mob-/NPC-Poolbelegung wird weiterhin nicht aus dem Prozessspeicher geschätzt. Sobald Logs jedoch `ShinePlayer full`, `Too many mob`, `Too many npc`, `Too many mapcluster`, `too many block info` oder `Too many BlockDistribute` melden, wird die betroffene Zone als kritisch behandelt.

Die Betriebs-Schwellen 70/85/95 % sowie das 3072-MiB-Private-Memory-Budget sind Management-Schwellen und ausdrücklich keine zusätzlichen aus der Binary abgeleiteten Hardlimits.
