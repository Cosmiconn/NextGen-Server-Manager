# 0.3.3 – Adaptive Hooks

0.3.3 turns the vertical-scaling audit into a guarded tuning layer.

## What can be applied now

- **WorldManager client sessions**: `ServerInfo.txt` hard-cap plus an optional live admission hook for the exact verified NA2016 `WorldManager.exe`.
  - Baseline SHA-256: `23e94c78840a80f874adffa15792ae29f5d25df950418b3633f6e5f5ba68ede1`
  - `g_UserLimit` stock VA `0x00556BAC` / RVA `0x00156BAC`
  - `m_MaxSessions` RVA `0x00156BB8`
  - `m_NumSessions` RVA `0x00156BBC`
  - `CWMClientSessionManager::InitSessions(maxSessions)` dynamically allocates `maxSessions × 0x1F7B8 + 4` bytes for its session array.
  - Therefore 1500 → 3000 sessions adds about 184.5 MiB of raw session-array memory before secondary allocations.
- **WorldManager Zone/S2S nMaxAccept**: transactional `ServerInfo.txt` config hook.
- **Zone client nMaxAccept**: transactional config hook, but it is deliberately capped at 1500 until the ShinePlayer binary-pool hook is fully verified.

All config writes are backed up under:

`<ServerRoot>/.nextgen-backups/adaptive-hooks/<timestamp>/`

The active profile is recorded under:

`<ServerRoot>/.nextgen-hooks/adaptive-profile.json`

## Adaptive warnings

The audit uses both process and whole-PC pressure:

- per-process CPU as percent of one logical core,
- WorldManager/Zone private memory,
- projected extra raw pool memory,
- whole-system CPU,
- whole-system available physical RAM,
- configurable warn/block thresholds.

Default balanced thresholds are 70% core CPU warning / 90% blocking and 75% / 90% of the conservative 32-bit private-memory budget.

## Zone hard-pool binary hooks

ShinePlayer, ShineMob and ShineNPC targets are configurable and their projected memory is shown, but automatic binary writes remain **guarded/blocked** in 0.3.3.

Reason: the NA2016 Zone uses a shared 16-bit object-handle namespace. Increasing the Mob count changes the base of Player and later object types; increasing Player/NPC also changes downstream ranges. The verified binary contains both allocation/count constants and separate handle range/base constants. A partial patch could produce handle collisions or wrong object resolution even if the process starts.

The manager therefore refuses to pretend that replacing one `1500`, `8000` or `256` constant is sufficient. The next binary-hook milestone is a complete hash-bound rebase matrix with exact expected-byte verification for every affected site.
