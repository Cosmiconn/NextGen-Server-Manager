# NA2016 Zone – Player/Mob/NPC Control-Flow Audit

Stand: 2026-10-06

## Zweck

Dieses Dokument ergänzt `ZONE_HANDLE_REBASE.md`. Es trennt echte Player-/Mob-/NPC-Poolabhängigkeiten von numerisch ähnlichen Konstanten und belegt, welche Spawn-/Regenerationspfade tatsächlich über den zentralen `ShineObjectManager` laufen.

Verifizierter Zone-Build:

- SHA-256: `db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5`
- `ShineObjectManager`: bevorzugte VA `0x132826B8`
- `ShineObjectManager::som_AllocObject`: VA `0x0054FE20`

**Status:** Nur Lesen/Audit/Dry-Run. Der Binär-Schreibpfad bleibt gesperrt.

## Aktueller Verifikationsstand

Der Pool-Rebase-Audit besteht derzeit aus drei getrennten Schichten:

1. **69 Core-Sites** – Pool-Maxima, generische/klassenspezifische Handle-Erzeuger, alle zentralen HandleSplit-Grenzen und Localize-Subtraktionen.
2. **14 zusätzliche echte Abhängigkeiten** – Player/NPC/Mob Backing-Allokationen, Elementzahlen, Initialisierungsspannen sowie Player-Quest-Buffer und Diagnosewert.
3. **13 Control-Flow-/Klassifikationsbelege** – repräsentative zentrale Allocator-Pfade, zwei nachgewiesene 256-False-Positives und die getrennte 1500er ShinePet-Familie.

Die 69 Core-Sites und 14 zusätzlichen Abhängigkeiten wurden auf der Original-`Zone.exe` jeweils bytegenau bestätigt. Die Control-Flow-Belege werden durch `ZonePoolControlFlowAudit` ebenfalls hash- und adressgebunden geprüft.

Die 13 Control-Flow-Belege sind **keine zusätzlichen Patchstellen**. Sie verhindern, dass falsche Zahlenwerte blind in die Patchmatrix aufgenommen werden.

## NPC: kein separates NPCManager-256-Limit gefunden

### `NPCManager::nm_SetNPC`

Bei VA `0x004C6645` wird Type-Code `4` (`ShineNPC`) vorbereitet und anschließend über die globale Managerinstanz `0x132826B8` `som_AllocObject` aufgerufen. Erst wenn dieser zentrale Allocator `NULL` zurückliefert, folgt die Meldung:

`NPCManager::nm_SetNPC : Too many npc`

Damit liegt die Belegungsentscheidung in diesem Pfad im zentralen NPC-Pool und nicht in einem zweiten lokalen `NPCManager`-Array mit eigenem 256er Hardlimit.

### `NPCManager::nm_DynamicRegenerateNPC`

Der gleiche Befund gilt bei VA `0x004C6EAC`: Type-Code `4` geht an `som_AllocObject`; die Meldung `Too many npc` folgt auf einen fehlgeschlagenen zentralen Allocationsversuch.

### Pine NPC-Pfad

`PineEventScriptNode::SysFuncShineNPCStand` nutzt bei VA `0x004E3B38` ebenfalls Type-Code `4` und denselben zentralen Allocator.

## Mob: mehrere unabhängige Spawnpfade delegieren zentral

Bytegenau nachgewiesene Beispiele:

| Pfad | VA | Type | Verhalten |
|---|---:|---:|---|
| GuildTournament Mob-Spawn | `0x0047C580` | 5 | `som_AllocObject` |
| MobBreeder Regen | `0x004B2D56` | 5 oder 8 | zentraler Allocator nach dynamischer Typwahl |
| Pine `SysFuncShineMobRegen` | `0x004E38B8` | 5 | `som_AllocObject` |
| Pine `ShineExchange2Mob` | `0x004ED74B` | 5 | `som_AllocObject` |

Damit werden die typischen `Too many mob`-Fehler nicht durch jeweils eigene 8000er Zähler in diesen Subsystemen ausgelöst. Sie sind Folge eines zentral gescheiterten Objekt-Allocationsversuchs.

Nicht jede Textmeldung mit `Too many mob` bezeichnet einen ShineMob-Pool. Meldungen wie `Too many mob in regengroup` oder `Too many MobRegenGroup` betreffen eigene Regen-/Gruppenstrukturen und dürfen nicht mit dem ShineMob-Pool verwechselt werden.

## 256-False-Positives

Die beiden auffälligen Werte bei:

- `0x005610B9`
- `0x0056115F`

sind **keine ShineNPC-Limits**. Der unmittelbare Kontrollfluss ist jeweils:

1. Stringlänge bestimmen,
2. `0x100` laden,
3. Länge gegen 256 vergleichen,
4. String/Packetdaten kopieren.

Diese Werte bleiben auch bei einem späteren Ziel `ShineNPC=512` unverändert.

## Player 1500 vs. Pet 1500 eindeutig getrennt

Die zweite 1500er-Familie gehört ShinePet und darf bei einer reinen Player-Erhöhung nicht verändert werden:

| Beleg | VA | Nachweis |
|---|---:|---|
| generischer Pet-Handle-Erzeuger | `0x00549046` | Limit 1500, Basis `0x5C56` |
| klassenspezifischer Pet-Handle-Erzeuger | `0x00557320` | Limit 1500, Basis `0x5C56` |
| ObjectManager-Konstruktor | `0x0055782C` | Liste `+0x1CC`, 1500 |
| `som_Initialize` Pet-Backing | `0x0055CEEB` | 1500 Elemente, Stride `0x25D4` |

Damit gilt für ein Profil wie Player=2000 / Mob=12000 / NPC=512 ausdrücklich:

- Player-bezogene 1500er werden angepasst,
- Pet bleibt 1500,
- ein globales Ersetzen aller `0x5DC` wäre falsch und gefährlich.

## Player-Quest-Buffer

Der Player-Pool hat zusätzlich zur Objektliste eine echte Hilfsstruktur: die Quest-Buffer-Initialisierung bei `0x005AF1CC` iteriert über die Stock-1500 Player-Slots. Wird Player später auf 2000 erhöht, muss auch diese Cardinality mitgeführt werden. Andernfalls hätten die zusätzlichen Player-Slots keinen vollständig initialisierten Quest-Buffer.

`0x005AE571` hält außerdem den Diagnosewert für `Player Buffer size : %d`; dieser ist nicht selbst der Pool-Gatekeeper, soll aber synchron bleiben.

## 8000-False-Positives

Die Zahl `8000` / `0x1F40` kommt in `Zone.exe` mehrfach unabhängig vom Mob-Pool vor. Bereits klassifizierte Beispiele:

- `0x0063E12C`: separater 16-Bit-Wert/Sentinel-Pfad, kein ShineMob-Limit.
- `0x0067CB6E` und `0x006814A6`: fortlaufende Member-Offset-Tabelle (`...0x1F38, 0x1F40, 0x1F48...`).
- mehrere Save-/Packet-/Inventory-/Quest-Pfade verwenden 8-KB-Buffer.

Der Wert `0x00559A7B = 8000` bleibt als separater Buffer-/Helper-Kandidat in der Restklassifizierung und wird **nicht** automatisch als Mob-Abhängigkeit behandelt.

## Was noch offen ist

Bevor `CoverageComplete` auf `true` gesetzt werden darf, bleiben insbesondere:

1. Restklassifizierung sämtlicher semantisch plausibler Player-/Mob-/NPC-Grenzwerte außerhalb der bereits erfassten 83 echten Patchstellen.
2. Vollständiger Call-Graph-Gegencheck für weitere spezialisierte Mob-/NPC-Erzeuger, die nicht durch die bisherigen repräsentativen Pfade abgedeckt sind.
3. Klare Klassifizierung verbleibender 8000-/1500-/256-Kandidaten wie `0x00559A7B`.
4. Erst danach: transaktionaler Patch-Builder auf **Kopie** der EXE, niemals direkt auf dem aktiven Original.
5. Backup/Manifest/Hash/Rollback und Post-Restart-Runtimeprüfung der tatsächlichen Pool-Maxima.

Bis dahin bleiben ShinePlayer/ShineMob/ShineNPC in Adaptive Hooks weiterhin **BLOCKIERT**.
