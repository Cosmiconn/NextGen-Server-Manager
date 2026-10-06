$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem -Path $root -Filter 'NextGen.Fiesta.ServerManager.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*publish*' } |
    Select-Object -First 1
if (-not $exe) { throw 'Keine veröffentlichte EXE gefunden. Zuerst Build.ps1 ausführen.' }
Start-Process $exe.FullName -Verb RunAs
