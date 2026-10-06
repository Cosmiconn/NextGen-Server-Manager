# NA2016 Log Discovery (0.2.8)

The manager no longer assumes that a service only writes `Message.txt` / `Dbg.txt` in its root folder.
Real NA2016 installations commonly keep additional logs below service folders such as Zone00, Zone03,
WorldManager, Login, Character, Account, AccountLog and GameLog.

## Recursive discovery

For every detected service directory the scanner now walks subdirectories (default depth: 8).
It recognizes text-like log files with these extensions:

- `.txt`
- `.log`
- `.err`
- `.out`
- `.trace`
- `.dbg`

A file is included when either:

1. it has a classic log name (`Message.txt`, `Dbg.txt`, `Error.txt`, etc.),
2. its filename contains a log/error/debug/network token, or
3. it is stored below a log-like directory such as `DebugMessage`, `Logs`, `Errors`, `Crash`, `Trace`, etc.

This means date/sequence-only files inside a `DebugMessage`/`Logs` directory are still discovered.

## Full analysis

Default limits are intentionally bounded for responsiveness:

- scan depth: 8 directories
- up to 32 newest discovered logs per service
- up to 2500 tail lines per analyzed file

These values live in `AppSettings` and can be made configurable in the GUI later.
The status bar reports how many files were discovered/analyzed and how many lines were inspected.

## Live monitor

The live monitor uses a recursive `FileSystemWatcher` plus a polling fallback. It watches the complete
service subtree and automatically attaches to newly created/rotated matching logs.

## Exclusions

To avoid scanning generated/cache/build content, these directory names are skipped:

- `.nextgen-cache`
- `.git`
- `bin`
- `obj`
- `backup`
- `backups`

