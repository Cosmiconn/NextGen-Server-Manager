# NA2016 Zone – 16-Bit Object Handle Rebase

Stand: 2026-10-06

## Zweck

Dieses Dokument ist die verbindliche Sicherheitsgrundlage für spätere ShinePlayer-/ShineMob-/ShineNPC-Pool-Erhöhungen in `Zone.exe`.

**Wichtig:** Der aktuelle Stand erlaubt Lesen, Bewerten und Dry-Run-Planung. Er erlaubt noch **keinen** automatischen Binärpatch. Ein einzelnes Ersetzen von `1500`, `8000` oder `256` wäre unsicher, weil die Objektpools einen gemeinsamen 16-Bit-Handle-Raum verwenden und jede Größenänderung nachfolgende Handle-Basen verschiebt.

Verifizierter Zone-Build:

- SHA-256: `db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5`
- bevorzugte Image Base: `0x00400000`
- `ShineObjectHandleUnion::sohu_HandleSplit`: VA `0x00633650`, RVA `0x00233650`

## Vollständige Stock-Matrix

| Typ | Type-Code | Stock-Bereich | Größe |
|---|---:|---:|---:|
| ShineMob | 5 | `0x0000-0x1F3F` | 8000 |
| ShinePlayer | 2 | `0x1F40-0x251B` | 1500 |
| ShineEffectObject | 3 | `0x251C-0x2903` | 1000 |
| ShineDropItem | 1 | `0x2904-0x34BB` | 3000 |
| ShineAxialFlag | 0 | `0x34BC-0x42BB` | 3584 |
| ShineNPC | 4 | `0x42BC-0x43BB` | 256 |
| ShineBandit | 8 | `0x43BC-0x4BBB` | 2048 |
| ShineMiniHouse | 9 | `0x4BBC-0x4FA3` | 1000 |
| ShineMagicField | 6 | `0x4FA4-0x509D` | 250 |
| ShineDoor | 7 | `0x509E-0x5485` | 1000 |
| ShineServant | 10 | `0x5486-0x5679` | 500 |
| ShineMover | 11 | `0x567A-0x5A61` | 1000 |
| **reserviert / ungültig** | – | `0x5A62-0x5C55` | 500 |
| ShinePet | 12 | `0x5C56-0x6231` | 1500 |

Erster unbenutzter Stock-Handle: `0x6232`.

Der 500er Bereich zwischen Mover und Pet ist **kein unbekannter Pool**. `sohu_HandleSplit` weist ihn ausdrücklich als ungültig zurück. Der Dry-Run-Planer erhält diese 500 Handles deshalb bewusst als reservierte Lücke.

## Zuordnung zum ShineObjectManager

Die zentrale Handle-Dekodierung führt zu folgenden Listen im `ShineObjectManager`:

| Type-Code | Manager-Liste | Manager-Offset |
|---:|---|---:|
| 0 | som_AxialFlag | `+0x4C` |
| 1 | som_DropItem | `+0x6C` |
| 2 | som_Player | `+0xAC` |
| 3 | som_Effect | `+0x8C` |
| 4 | som_NPC | `+0xCC` |
| 5 | som_Mob | `+0x10C` |
| 6 | som_MagicField | `+0x14C` |
| 7 | som_Door | `+0xEC` |
| 8 | som_Bandit | `+0x12C` |
| 9 | som_MiniHouse | `+0x16C` |
| 10 | som_Servant | `+0x18C` |
| 11 | som_Mover | `+0x1AC` |
| 12 | som_Pet | `+0x1CC` |

`som_GetObject`, `som_GetObjectAbsolute` und `som_FreeObject` benutzen diese zentrale Handle-Aufteilung. Dadurch ist die Dekodierlogik zentral, aber die Handle-Erzeuger und Pool-Initialisierungen bleiben zusätzliche Patch-Abhängigkeiten.

## Verifizierte Pool-Initialisierung

Der `ShineObjectManager`-Konstruktor initialisiert die Maximalgrößen mit den Stock-Werten:

- AxialFlag 3584
- DropItem 3000
- Effect 1000
- Player 1500
- NPC 256
- Door 1000
- Mob 8000
- Bandit 2048
- MagicField 250
- MiniHouse 1000
- Servant 500
- Mover 1000
- Pet 1500

Eine sichere Vergrößerung muss daher mindestens die betroffenen Konstruktor-/Initialisierungswerte zusammen mit den Handle-Grenzen ändern.

## Verifizierte Handle-Erzeuger

Die typspezifischen Funktionen prüfen jeweils den lokalen Index gegen die Poolgröße und addieren anschließend die globale Handle-Basis:

| Typ | VA | Stock-Limit | Stock-Basis |
|---|---:|---:|---:|
| Mob | `0x00548E00` | 8000 | `0x0000` |
| Player | `0x00548E30` | 1500 | `0x1F40` |
| EffectObject | `0x00548E60` | 1000 | `0x251C` |
| DropItem | `0x00548E90` | 3000 | `0x2904` |
| AxialFlag | `0x00548EC0` | 3584 | `0x34BC` |
| Bandit | `0x00548EF0` | 2048 | `0x43BC` |
| NPC | `0x00548F20` | 256 | `0x42BC` |
| MiniHouse | `0x00548F50` | 1000 | `0x4BBC` |
| MagicField | `0x00548F80` | 250 | `0x4FA4` |
| Door | `0x00548FB0` | 1000 | `0x509E` |
| Servant | `0x00548FE0` | 500 | `0x5486` |
| Mover | `0x00549010` | 1000 | `0x567A` |
| Pet | `0x00549040` | 1500 | `0x5C56` |

Zusätzliche klassenspezifische Basis-Erzeuger wurden ebenfalls gefunden, u. a. Player `0x00559D71`, NPC `0x00557CA1`, Bandit `0x00556F01`, Pet `0x00557341`. Eine Änderung muss **alle** verifizierten Erzeuger berücksichtigen, nicht nur die generische Funktionstabelle.

## Konsequenz für Player / Mob / NPC

- **Mob wächst:** jede nachfolgende Basis verschiebt sich.
- **Player wächst:** EffectObject und alle späteren Bereiche verschieben sich.
- **NPC wächst:** Bandit und alle späteren Bereiche verschieben sich.
- Die reservierte 500er Lücke wird im aktuellen Sicherheitsmodell beibehalten.
- Die gesamte neue Matrix muss innerhalb `0x0000-0xFFFF` liegen.

Beispiel für das derzeitige Balanced-Ziel `Mob=12000`, `Player=2000`, `NPC=512`: Der Layout-Dry-Run ist grundsätzlich im 16-Bit-Raum darstellbar; das allein macht den Binärpatch **noch nicht** sicher. Erst sämtliche Patchstellen müssen bytegenau nachgewiesen werden.

## Freigabe-Gates für einen echten Patch

Ein Schreibpfad darf erst implementiert/freigegeben werden, wenn alle folgenden Punkte erfüllt sind:

1. Exakter Zone.exe-SHA-256 stimmt mit dem verifizierten Build überein.
2. Zielmatrix ist durch `ZoneHandleLayout` gültig, überlappt nicht und endet <= `0xFFFF`.
3. Sämtliche `HandleSplit`-Grenzen sind als erwartete Originalbytes eindeutig gefunden.
4. Sämtliche Pool-Maxima im Konstruktor sind eindeutig gefunden.
5. Sämtliche generischen Handle-Erzeuger (Limit + Basis) sind eindeutig gefunden.
6. Sämtliche klassenspezifischen Basis-/Range-Stellen sind erfasst.
7. Weitere direkte Vergleiche/Loops mit den Stock-Grenzen sind ausgeschlossen oder in der Patchmatrix enthalten.
8. Preflight verlangt für **jede** Patchstelle exakt die erwarteten Originalbytes; fehlend oder mehrfach => BLOCKIERT.
9. Patch wird atomar in eine neue Datei geschrieben, vollständig rückgelesen/verifiziert und erst dann ersetzt.
10. Original wird mit Manifest/Hash gesichert; Rollback ist zwingend.
11. Nach Neustart werden die Runtime-Maxima und Live-Belegungen erneut gelesen und müssen dem Profil entsprechen.

Bis diese Gates erfüllt sind, bleiben ShinePlayer/ShineMob/ShineNPC in Adaptive Hooks korrekt auf **BLOCKIERT**.
