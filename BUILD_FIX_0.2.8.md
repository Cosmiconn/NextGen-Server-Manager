# 0.2.8 - Recursive NA2016 Log Discovery

- Replaces the old top-level-only log scan.
- Recursively discovers text-like logs below every service folder.
- Adds support for nested DebugMessage/Logs/Error/Crash/Trace folders.
- Supports `.txt`, `.log`, `.err`, `.out`, `.trace`, `.dbg`.
- Live monitor now watches subdirectories and rotated/new files recursively.
- Full analysis checks up to 32 recent logs per service and 2500 tail lines per file by default.
- Log panel shows how many log files were discovered for the selected service.
- Diagnose status shows discovered/analyzed log counts and inspected line count.
- Bounded streaming tail avoids loading huge historical logs completely into RAM.
