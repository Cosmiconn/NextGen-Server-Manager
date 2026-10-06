# 0.3.3 – Adaptive Hooks

## Added

- Configurable Adaptive Hook profiles and thresholds.
- Whole-system CPU and physical RAM pressure sampling.
- Transactional ServerInfo hooks with backup/rollback.
- Exact-baseline WorldManager runtime admission hook (`g_UserLimit`).
- Runtime verification against `m_MaxSessions` and `m_NumSessions` before writing.
- WorldManager session memory projection from verified stride `0x1F7B8`.
- Hook manifest and restore-last-backup action.
- Guard rails that refuse incomplete Zone ShinePlayer/Mob/NPC binary pool patches.

## Validation performed in the build environment

- WPF XAML and csproj parse as XML.
- C# source brace/static structure scan passed.
- Stock `ServerInfo.txt` matching was tested against the supplied `NA2016-main.zip`: WM client/S2S/OPTool and all five stock Zone client rows are recognized.
- Exact supplied binaries were re-hashed:
  - WorldManager.exe SHA-256 `23e94c...68ede1`
  - Zone.exe SHA-256 `db1cb4...11aff5`
- The exact WorldManager binary was re-disassembled at `CWMClientSessionManager::InitSessions`: session stride `0x1F7B8` and dynamic allocation by `maxSessions` are present.

A native Windows/.NET WPF compile/run could not be executed in the Linux artifact environment because the `dotnet` command is not installed there. Run `scripts\Build.ps1` on the target Windows machine before deployment.
