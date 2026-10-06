# Zone Pool Hook Research – NA2016 baseline

Baseline Zone.exe SHA-256: `db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5`

Die weitere Disassembly bestätigt, dass die bekannten Shine-Pools gemeinsam in einem zentralen Initialisierungsblock angelegt werden. Im Preferred-VA-Bereich um `0x0055C260` liegen Count und Stride direkt im Code. Für den exakten Baseline-Build wurden folgende Paare erneut bestätigt:

| Pool | Count | Stride | relevante VA |
|---|---:|---:|---:|
| ShineAxialFlag | 3584 | 0x188 | ~0x55C2E9 |
| ShineDropItem | 3000 | 0x28B | ~0x55C3DC |
| ShineEffectObject | 1000 | 0x1D3 | ~0x55C4D4 |
| ShinePlayer | 1500 | 0x2C058 | ~0x55C5D6 |
| ShineNPC | 256 | 0x256C | ~0x55C6D8 |
| ShineBandit | 2048 | 0x266C | ~0x55C7DA |
| ShineDoor | 1000 | 0x1F14 | ~0x55C8DD |
| ShineMob | 8000 | 0x2568 | ~0x55C9DF |
| ShineMagicField | 250 | 0x1C8 | ~0x55CAE1 |
| ShineMiniHouse | 1000 | 0xD100 | ~0x55CBE4 |
| ShineServant | 500 | 0x2598 | ~0x55CCE6 |
| ShineMover | 1000 | 0x202C | ~0x55CDE8 |
| ShinePet | 1500 | 0x25D4 | ~0x55CEEB |

Diese Liste beweist die Allokationsstellen, **noch nicht** die vollständige 16-Bit-Handle-Rebase-Matrix. Die Handle-Basen/Range-Checks werden in separaten Codepfaden verwendet. Deshalb bleiben automatische ShinePlayer/Mob/NPC-Binary-Hooks in 0.3.4 weiterhin gesperrt.

Nächste Verifikation:

1. alle Handle-Type→Base/Count-Konvertierungen finden,
2. kumulative Basen je Typ beweisen,
3. sämtliche `ushort`-Rangechecks und Sentinel `0xFFFF` berücksichtigen,
4. Gesamtbereich < 65535 erzwingen,
5. atomaren Patchsatz mit Originalbytes + erwarteten Replacementbytes definieren,
6. nur hashgebunden anwenden und bei jeder Abweichung abbrechen.
