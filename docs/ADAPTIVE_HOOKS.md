# Adaptive Hooks – NA2016

## Safety model

1. Analyze live CPU/RAM and current session pressure.
2. Verify the exact executable SHA-256 before any address-dependent operation.
3. Produce a recommendation: `EMPFOHLEN`, `WARNUNG`, or `BLOCKIERT`.
4. Backup `ServerInfo.txt` before any configuration hook.
5. Apply only the safe subset.
6. Never modify stock Zone binaries unless the complete dependent patch set has been verified.
7. Keep a manifest of every applied profile.

## WorldManager live hook

For the exact supplied NA2016 WorldManager build the manager reads the running process using module-base + RVA so ASLR relocation is handled correctly.

The runtime write changes **only** `g_UserLimit`. It is allowed only when:

- the executable SHA-256 matches the verified baseline,
- `target <= m_MaxSessions`,
- `target >= current m_NumSessions`,
- the process can be opened with the required rights.

Raising `m_MaxSessions` itself is done by changing the WM client `nMaxAccept` in `ServerInfo.txt` and restarting WorldManager, because `InitSessions(maxSessions)` allocates the session array during startup.

This keeps runtime writes minimal and reversible: the durable hard-cap remains visible in normal server configuration.

## Why Zone hard pools are still guarded

The verified Zone binary contains a contiguous object-handle layout. Examples from the current baseline include:

- Mob: count 8000, base 0
- Player: count 1500, base 8000
- additional types use cumulative bases after those ranges

Pool initialization and handle/index conversion are separate code paths. Therefore a safe higher Mob/Player/NPC limit must patch all dependent allocations, counts, range checks and cumulative bases as one atomic versioned profile.

0.3.3 already evaluates the requested targets against CPU/RAM so the UI can tell whether the hardware would justify the change. It does not write an incomplete binary patch.
