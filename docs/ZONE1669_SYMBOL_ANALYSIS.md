# NA2016 Zone00 – PDB forensic analysis of so_Disconnect[1669]

Status: 2026-10-11. Original Zone/WorldManager binaries **not patched**. This is forensic evidence, not a runtime fix.

## Proven original-binary identity

Provided PE RSDS debugger records exactly match provided PDB Info streams:

| Component | PDB GUID | Age | Verified |
| --- | --- | ---: | --- |
| Zone.exe / Zone.pdb | `339124ac-7257-466d-ac43-793bb53692ab` | 16 | yes |
| WorldManager.exe / WorldManager.pdb | `5af524ea-700f-4106-81d5-b7236f16f57c` | 10 | yes |

Zone.exe is a 32-bit PE with image base 0x00400000 and .text section RVA 0x1000. All addresses below are **RVAs** relative to the PE image; never reuse absolute addresses without checking the live loaded module and matching binary/PDB GUID.

## Root trigger proven by Zone.exe disassembly + matched Zone.pdb

```text
ShineMobileObject::so_Routine (RVA 0x0016A080)
       direct call to smo_SkillBash_Blast at RVA 0x0016A858
     -> ShineMobileObject::smo_SkillBash_Blast (RVA 0x0017CD40)
       direct call to smo_ShadowStep at RVA 0x0017CFDA
     -> ShineMobileObject::smo_ShadowStep (RVA 0x0005AD10)
        -> RadianMoveTrack::rmt_Calculate (RVA 0x00060450)
        -> FieldMap::fm_Marking (RVA 0x00052FD0)
           check return value at RVA 0x0005AE56
           return != 0 -> virtual Disconnect(1669, 0, 0)
           literal push 0x685 at RVA 0x0005AE66 (1669 decimal)
     -> ShinePlayer::so_Disconnect (RVA 0x0014F340)
```

Verified callsite instructions in the original Zone.exe (shown as loaded VA using image base 0x00400000):

```asm
0045AE42: push 1
0045AE44: push 0
0045AE46: push 0
0045AE50: push esi
0045AE51: call 00452FD0 ; FieldMap::fm_Marking
0045AE56: test eax,eax
0045AE58: je 0045AE74 ; no failure
0045AE5A: mov eax,[esi]
0045AE5C: mov edx,[eax+0x5F8]
0045AE62: push 0
0045AE64: push 0
0045AE66: push 0x685 ; reason 1669, not ShinePlayer pool limit
0045AE6B: mov ecx,esi
0045AE6D: call edx ; virtual Disconnect
```

The PDB TPI stream contains `FieldMap::FM_MarkingError` enum index `0x5436` and fieldlist index `0x5435`:

| Value | Enum |
| --- | --- |
| 0 | FM_MARKSUCCESS |
| 1 | FM_MAPNOTEXIST |
| 2 | FM_OTHERMAP |
| 3 | FM_BLOCKED |

`FieldMap::fm_Marking` compares the object's X coordinate with `FieldMap+0x18` (XSize) and Y with `FieldMap+0x1C` (YSize); a greater-than unsigned jump reaches a return value of **3** (`FM_BLOCKED`). For the specific `smo_ShadowStep` call `fm_Marking(object,0,0,1)`, a successful bounds check enters a direct success branch, bypassing the later, more involved marking/collision logic.

**High-confidence conclusion:** The logged `so_Disconnect[1669]` is produced by the ShadowStep error branch. For this argument combination, an **out-of-bounds X/Y relative to the current FieldMap** is the specifically demonstrated failure mechanism; `FM_BLOCKED` here must not be casually interpreted as player-pool exhaustion or another player's occupied tile.

**Upstream trigger:** `smo_SkillBash_Blast` is directly dispatched in `ShineMobileObject::so_Routine` (VA `0x0056A858`, RVA `0x0016A858`); this is an in-game mobile-object routine pathway, not the dedicated network `sp_NC_MAP_LOGIN_REQ` handler. Only one direct static `call` of this ShadowStep implementation was found by disassembling the EXE .text, from `smo_SkillBash_Blast`. This points to knockback/shadow-motion handling. Indirect calls elsewhere cannot be excluded without runtime tracing. **Not yet determined:** the actor/skill initiating the bash, the actual X/Y and map bounds, or whether a scripted AI behavior causes this during login.

## Observational corroboration (not the source of the disassembly claim)

The uploaded `Log.zip` contains `MoreDisconnectLog2026101102.txt`: for `NGL000985`, `Char so_Disconnect NGL000985 1669` at 02:12:16, :19, :22 and :25 MESZ. The corresponding `CoordRepairLog` events at those seconds are `TSStart/14700/8346/2` (first two) and `TSStart/14101/8000/2` (next two). Do **not** assume that the TSStart repair/reference coordinates are the actual invalid X/Y from the failed movement. The client ramp trace documents 4 remote closes before SH6/2. Other accounts passed after a fresh Zone handoff.

Older `ZoneLinktoError_2026-10-09 12.txt` `Invalid Regnum` errors belong to `ShinePlayer::so_SetRegistNumber` (RVA 0x0004E900) and have no established temporal causal relationship with the October 11 disconnects.

The vast majority of Win32 `CSocket_IOCP::Write err=64` entries occurred during cleanup after the successful 1600-player run. The separate `KingdomQuest Buffer full[0]` warning likewise remains unresolved, not evidence for a marking error.

## Next safe investigation

1. **On a separate test server**, capture the failure-specific return from `fm_Marking` at RVA `0x0005AE56` (not the global 1600-player benchmark) with object X, Y (`ShineMobileObject+0x66` points to the coordinates), `FieldMap+0x18/+0x1C` bounds, map identity, character ID, and call stack. Runtime instrumentation/debugger can pause or distort performance; do not attach an unconditional high-frequency breakpoint during capacity certification.
2. Trace `smo_SkillBash_Blast` to the initiating skill, attacker, target, and knockback movement vector; confirm or refute whether the new player's position is pushed beyond bounds.
3. Reproduce with controlled spawn/skill environment without silently bypassing server checks, changing authoritative DB data, or adjusting Zone/WM hardlimits. Keep the strict CH6/1 -> SH6/2 -> CH6/3 -> exact ShinePlayer proof.
4. Track KingdomQuest MAKE buffer warnings and mass-disconnect IOCP errors **as separate issues**.
5. Before making any code or binary adjustment, verify the exact live original PE hash/PDB GUID match and keep the accepted main baseline untouched.

Related: [SCALING_AND_PROVISIONING.md](SCALING_AND_PROVISIONING.md), the stored 1600-pass and 900-pass ramp traces, the standalone detailed PDB audit report from 2026-10-11.
