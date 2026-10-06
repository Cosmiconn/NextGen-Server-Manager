# 0.2.7 – PDB search + limit audit

- Fixes PDB search button staying disabled after typing.
- Enter in the PDB search field now runs the search.
- Search output reports number of indexes and matches.
- PDB indexing now also discovers `*.pdb` recursively below the selected Server Root, not only files directly beside known services.
- Adds `docs/NA2016_LIMITS.md` with binary/PDB-verified NA2016 limits and a clear distinction between `nBackLog`, `nMaxAccept` and hard-coded gameplay buffers.
