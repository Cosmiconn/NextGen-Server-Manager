# 0.2.6 – LLVM/PDB detection fix

- `llvm-pdbutil.exe` is no longer discovered only through `where.exe`/PATH.
- Auto-detection now checks the official LLVM paths under Program Files, Program Files (x86), LocalAppData and Chocolatey.
- Optional overrides: `NEXTGEN_LLVM_PDBUTIL` or `LLVM_PDBUTIL` can point directly to llvm-pdbutil.exe.
- `Check-Prerequisites.ps1` uses the same fallback detection logic.
- PDB cache names are now unique per service directory so Zone00..ZoneNN do not overwrite each other's `Zone.pdb` symbol cache.
- Successful PDB indexing shows the actual llvm-pdbutil path in the Details column.
