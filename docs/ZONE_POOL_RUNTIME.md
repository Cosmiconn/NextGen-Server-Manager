# Zone Runtime Pool Counters – NA2016

Stand: 2026-10-06

Ziel dieses Blocks ist ausschließlich die **sichere Laufzeitmessung** der Zone-Objektpools. Die hier dokumentierten Adressen werden nicht zum Schreiben/Patchen verwendet.

## Verifizierter Build

`Zone.exe` SHA-256:

`db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5`

Alle Runtime-Lesezugriffe sind an diesen Hash gebunden. Bei einem anderen Binary-Hash liefert der Manager keine als verifiziert markierte Poolbelegung.

## PDB-Strukturbeleg

`Zone.pdb` beschreibt `ShineObjectManager` mit Größe `0x220` und u. a. folgenden Feldern:

| Feld | Offset im ShineObjectManager |
|---|---:|
| `som_PlayerArr` | `0x24` |
| `som_NPCArr` | `0x28` |
| `som_MobArr` | `0x30` |
| `som_Player` | `0xAC` |
| `som_NPC` | `0xCC` |
| `som_Mob` | `0x10C` |

Die drei `som_*`-Listen sind `ShineObjectEachList`-Objekte. Diese erben von `List<ShineObjectClass::ShineObject>`.

Für diese Basisklasse weist das PDB nach:

| Feld | Offset im List-Objekt |
|---|---:|
| `l_MaxSize` | `0x04` |
| `l_Array` | `0x08` |
| `l_Finger` | `0x0C` |
| `l_ListArray` | `0x10` |
| `l_ListNum` | `0x14` |

`l_ListNum` ist der laufende Belegungszähler.

## Codebeleg für l_ListNum

Die Disassemblierung des exakt gehashten Zone-Builds bestätigt die Semantik:

- `List<ShineObject>::l_AllocZ` bei bevorzugter VA `0x005D08C0` erhöht `WORD PTR [ecx+0x14]` bei `0x005D09A8`.
- `List<ShineObject>::l_AllocA` bei `0x0054B1E0` erhöht `WORD PTR [ecx+0x14]` bei `0x0054B2C5`.
- `List<ShineObject>::l_Free` bei `0x004B1300` vermindert denselben Wert bei `0x004B13D1`.
- `ShineObjectManager::som_AllocObject` bei `0x0054FE20` routet die Objekttypen auf genau diese `som_*`-Listen.

Damit ist `l_ListNum` keine Schätzung und kein Logindikator, sondern die tatsächliche Anzahl belegter Listenslots.

## Globale Managerinstanz

PDB-Public-Symbol:

`?shineobjmanager@@3VShineObjectManager@@A`

Bevorzugte VA: `0x132826B8`  
Image Base: `0x00400000`  
ASLR-sicher verwendete RVA: `0x12E826B8`

Die Probe addiert diese RVA zur echten `MainModule.BaseAddress` des laufenden Zone-Prozesses.

## Daraus abgeleitete Runtimefelder

| Pool | `l_MaxSize` relativ zum Manager | `l_ListNum` relativ zum Manager | Stock-Limit |
|---|---:|---:|---:|
| ShinePlayer | `0xB0` | `0xC0` | 1500 |
| ShineNPC | `0xD0` | `0xE0` | 256 |
| ShineMob | `0x110` | `0x120` | 8000 |

Zusätzlich werden die Pool-Arrayzeiger (`+0x24`, `+0x28`, `+0x30`) auf Initialisierung geprüft. Die Probe markiert Werte nur dann als verifiziert, wenn:

1. der Dienst läuft und eine PID besitzt,
2. der Binary-Hash exakt passt,
3. alle drei Arrayzeiger gesetzt sind,
4. die gelesenen `l_MaxSize`-Werte exakt 1500 / 256 / 8000 ergeben,
5. kein `l_ListNum` größer als sein jeweiliges Maximum ist.

## Sicherheitsgrenze

Dieser Nachweis erlaubt **nur lesende Live-Counter**.

Er erlaubt ausdrücklich noch **nicht**, ShinePlayer, ShineMob oder ShineNPC zu vergrößern. Die Pools teilen sich ein 16-Bit-Handle-Adressschema. Eine Vergrößerung verschiebt nachfolgende Handle-Basen und ist erst zulässig, wenn die vollständige Rebase-Matrix samt allen abhängigen Bounds, Serialisierungen und Lookups nachgewiesen und als atomisches, rollbackfähiges, hashgebundenes Profil umgesetzt ist.
