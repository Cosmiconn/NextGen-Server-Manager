# NA2016 Client – Terrain- und Einzelkarten-Limits

Stand: 0.3.0

## Ergebnis in einem Satz

Für die analysierte NA2016 `Fiesta.bin` ist im eigentlichen HeightMap/HTD-Terrainloader **kein einfacher harter 512/1024/2048-Dimensionscheck** vorhanden. Das Terrain wird dynamisch aus 32-Bit-Breiten/Höhen aufgebaut. Trotzdem ist **512×512 Terrain-Quads das konservative stock-NA2016-Produktionsziel**. 1024×1024 ist nicht als stock-sicher einzustufen, weil außerhalb des Terrainloaders Koordinaten-/NPC-/Packetpfade und die 2016-Client-Performance zum Problem werden.

## Analysierter Client

Bekannte NA2016-Baseline:

- SHA-256: `01196b20abe4fb542ee8c4685f57224ef29051b2bf24196eed80a9b2a95ae4fb`
- PE32 / x86
- `LARGE_ADDRESS_AWARE` gesetzt
- Terrain-INI-Strings vorhanden: `HEIGHTMAP_WIDTH`, `HEIGHTMAP_HEIGHT`, `OneBlockWidth`, `OneBlockHeight`, `QuadsWide`, `QuadsHigh`, `HeightFileName`, `HeightMap`, `.HTD`

## Statische Loader-Evidenz

Relevante Bereiche der bekannten NA2016 `Fiesta.bin`:

- INI/Terrain-Parameterparser ungefähr ab `0x008A5B90`
- HeightMap/HTD-Loader ungefähr ab `0x008A64D0`
- Terrain-Block-/Descriptoraufbau ungefähr ab `0x008A7720`

Der Loader:

1. liest `HEIGHTMAP_WIDTH` und `HEIGHTMAP_HEIGHT` in 32-Bit-Felder,
2. alloziert drei dynamische `width × height × 4`-Byte-Puffer,
3. validiert beim HTD-Pfad den Datei-Count gegen `width × height`,
4. berechnet das Chunkraster aus `(width-1)/QuadsWide × (height-1)/QuadsHigh`,
5. alloziert die Blockdescriptoren dynamisch mit etwa `0x38` Byte pro Chunk.

Damit ist der **Terrainloader selbst** nicht der Beleg für einen 512er Hardcap.

## Speicherwachstum

Bei `QuadsWide=QuadsHigh=64`, `OneBlockWidth=50` und quadratischen Maps:

| Terrain | Heightmap-Punkte | Chunks | 3 rohe Floatpuffer | HTD ca. | Weltseite |
|---:|---:|---:|---:|---:|---:|
| 256 | 257² | 16 | 0.76 MiB | 0.25 MiB | 12,800 |
| 512 | 513² | 64 | 3.01 MiB | 1.00 MiB | 25,600 |
| 1024 | 1025² | 256 | 12.02 MiB | 4.01 MiB | 51,200 |
| 2048 | 2049² | 1024 | 48.05 MiB | 16.02 MiB | 102,400 |
| 4096 | 4097² | 4096 | 192.09 MiB | 64.03 MiB | 204,800 |

Das sind nur die drei direkt belegten Heightpuffer. Nicht enthalten sind Terrain-Geometrie, Gamebryo-Nodes, Blend-/Vertex-Texturen, NIFs, Mobs, NPCs, Effekte, Sichtbarkeitsstrukturen und weitere Client-Caches.

## 16-Bit-Koordinatenrisiko

Community-Arbeiten, die 1024×1024-Support in Fiesta nachgerüstet haben, berichten, dass mehrere ursprüngliche Koordinatenpfade `short` verwenden und für 1024 auf `int` umgestellt werden mussten; dabei mussten auch Packetserialisierung und serverseitige NPC-Logik angepasst werden.

Bei typischen `50.0` Weltunits pro Terrain-Quad ergibt sich daraus als **Inferenz**, nicht als universeller Hardcap:

- signed 16-bit Maximum: `32767`
- `655 × 50 = 32750` → noch innerhalb
- `656 × 50 = 32800` → darüber
- `1024 × 50 = 51200` → deutlich darüber

Wichtig: Nicht jeder Fiesta-Koordinatentyp ist 16 Bit. Es existieren auch 32-Bit-Koordinatenstrukturen. Die Grenze 655/656 ist deshalb ein Warnsignal für noch nicht auditierte Pfade, keine Behauptung, dass jede Map bei 656 sofort scheitern muss.

## Praktische Bewertung

- **bis 512×512:** konservatives stock-NA2016-Ziel; empfohlen für produktive Maps.
- **513–655:** experimentell; Terrainloader und signed-16-bit-Seitenlänge sprechen nicht unmittelbar dagegen, aber Header/Gameplaypfade müssen getestet werden.
- **ab 656:** erhöhte Koordinatenrisiken bei 50 Units/Quad.
- **1024×1024:** statisch vom Terrainloader darstellbar, aber **nicht stock-sicher**. Community-Berichte beschreiben den 2016-Client bei bevölkerten 1024er Maps als stark laggend bis nahezu unspielbar; separate 1024-Support-Arbeiten mussten Koordinatenpfade umbauen.
- **2048+:** nur Engine-/Protocol-Research für den unveränderten NA2016-Client. Kein sinnvoller Produktionswert ohne systematische Client-/Server-Patches und Lasttests.

## Warum die 950er SHBDs kein Gegenbeweis sind

Im Serverbestand gibt es `Adl.shbd`/`AdlVal01.shbd` mit Header `950 × 7600`, also einer Kollisionsfläche von `7600 × 7600` und einer serverseitig berechneten Blockfläche von `47,500 × 47,500`. Das ist ein **BlockInfo-/SHBD-Befund** und darf nicht automatisch als `950×950` Client-Heightmap interpretiert werden. `Field.txt`, SHBD und Client-HTD/INI bilden unterschiedliche Ebenen.

## Für einen echten absoluten Maximalwert fehlt noch

Ein absoluter „größter Wert, der niemals crasht“ lässt sich aus statischer Analyse allein nicht seriös ableiten. Dafür sind kontrollierte Runtime-Tests mit leerer und bevölkerter Map nötig, einschließlich:

- 512 als Referenz,
- 640/655 als 16-Bit-Grenznähe,
- 656/768 für Overflow-/Gameplaytests,
- 1024 für bekannte Problemzone,
- Überwachung von Client Working Set / Private Bytes / FPS / Ladezeit / Exceptions,
- NPC/Mob/Player-Wegfindung und Packettests an den Kartenrändern.

Der Server Manager 0.3.0 zeigt deshalb bewusst **Hard Evidence**, **Inference** und **praktische Empfehlung** getrennt an.

## Automatische Client-Inventur in 0.3.0

Wenn der ausgewählte Client einen `resmap`-Ordner enthält, scannt der Manager rekursiv HeightMap-INIs und liest `HEIGHTMAP_WIDTH/HEIGHT`, `OneBlockWidth/Height` sowie `QuadsWide/High`. Dadurch wird nicht nur eine theoretische Größenmatrix angezeigt, sondern auch die tatsächlich im installierten Client vorhandene größte Terrainmap samt Risikoeinstufung.
