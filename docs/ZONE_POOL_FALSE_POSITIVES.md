# NA2016 Zone – Pool False Positives

Stand: 2026-10-06

## Zweck

Die Stock-Poolgrößen `8000`, `1500` und `256` kommen in `Zone.exe` auch an Stellen vor, die **nichts** mit ShineMob, ShinePlayer oder ShineNPC zu tun haben. Diese Datei dokumentiert die gefährlichsten Doppelgänger. Sie sind hash- und adressgebunden durch `ZonePoolFalsePositiveAudit` abgesichert und müssen bei einem späteren Pool-Rebase unverändert bleiben.

Verifizierter Build:

- SHA-256: `db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5`

## 8000 / `0x1F40`

### `0x00559A7B` – Inventory-Füllbuffer, kein Mob-Pool

PDB-Zuordnung:

- Funktion: `ShinePlayer::so_fullbufferitem_box` bei `0x00559A70`
- Zielaufruf: `CharacterInventory::ci_FillBufferInventoryItem` bei `0x00643D40`

Der Wert `8000` wird als Bufferkapazität an die Inventory-Serialisierung übergeben. Er ist vollständig unabhängig vom ShineMob-Objektpool. Bei einem Ziel wie `Mob=12000` bleibt dieser Wert **8000**.

### `0x0063E12C` – Raid-Systemwert, kein Mob-Pool

PDB-Zuordnung: `RaidSystem::ResetRaid`.

Der Wert wird als 16-Bit Zustand/Protokollwert geschrieben. Er gehört nicht zum `ShineObjectManager` und bleibt unverändert.

### `0x0067CB6E` und `0x006814A6` – Member-Offsets

Der Disassembly-Kontext zeigt jeweils die Folge:

`0x1F38 -> 0x1F40 -> 0x1F48`

Das sind fortlaufende Objekt-/Member-Offsets in generierten Dispatch-/Unwind-nahen Tabellen. Die Dezimalgleichheit mit Mob=8000 ist rein zufällig.

## 1500 / `0x5DC`

### `0x0067A8C2` und `0x0067ED32` – Member-Offsets

Der Kontext zeigt:

`0x5D4 -> 0x5DC -> 0x5E4`

Auch hier ist `0x5DC` ein fortlaufender Member-Offset, kein Player- oder Pet-Limit. Eine Player-Erhöhung auf 2000 darf diese Werte nicht verändern.

Zusätzlich ist die separate echte ShinePet-1500er Familie im Control-Flow-Audit nachgewiesen. Pet bleibt bei einem reinen Player-Rebase auf 1500.

## 256 / `0x100`

### `0x0049E98C` – `MapBlockInformationBox::mbib_Load`

Diese Stelle vergleicht einen Map-/BlockInfo-Zähler gegen 256. Das ist das eigenständige NA2016-MapBlockInformation-Limit und **nicht** der ShineNPC-Pool.

Ein späteres Profil `NPC=512` darf diesen Wert nicht verändern. Map-/BlockInfo-Skalierung ist ein eigener Patch-/Research-Bereich.

### Weitere bereits ausgeschlossene 256er

`0x005610B9` und `0x0056115F` wurden separat als 256-Byte String-/Packet-Längenprüfungen in ShinePlayer-Funktionen nachgewiesen und sind ebenfalls keine NPC-Limits.

## Konsequenz

Ein sicherer Pool-Patcher darf niemals global nach `8000`, `1500` oder `256` suchen und ersetzen. Zulässig sind ausschließlich die im Rebase-/Dependency-Audit adressgebunden nachgewiesenen Patchstellen mit exakten Originalbytes.

Der False-Positive-Audit ist ein **Negativbeweis**: Er beschreibt Werte, die trotz gleicher Zahl ausdrücklich unverändert bleiben müssen. Er allein gibt keinen Schreibpfad frei.
