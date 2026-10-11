#requires -Version 5.1
<#
.SYNOPSIS
    Simplified Zone1669 capture for C:\NextGenFiesta\Server\Zone01\Zone.exe.
.DESCRIPTION
    Locates matching Zone.pdb, verifies exact PE/PDB via original preparer,
    locates the running Zone01 by its full executable path and (only on
    explicit operator confirmation) attaches WinDbg x86.
    Without classic WinDbg, prints/copies an Attach+Command for modern WinDbg.
#>
[CmdletBinding()]
param(
    [string]$ZonePath = 'C:\NextGenFiesta\Server\Zone01\Zone.exe',
    [string]$PdbPath,
    [string]$OutputDirectory = 'C:\NextGenZone1669Capture',
    [switch]$PrepareOnly,
    [switch]$SelfTest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$preparer = Join-Path $PSScriptRoot 'New-Zone1669OneShotCapture.ps1'

function Find-ZonePdb {
    param([string]$ZoneExe, [string]$Preferred)
    if (-not [string]::IsNullOrWhiteSpace($Preferred)) {
        if (-not (Test-Path -LiteralPath $Preferred -PathType Leaf)) {
            throw "Zone.pdb nicht gefunden: $Preferred"
        }
        return (Resolve-Path -LiteralPath $Preferred).Path
    }
    $serverFolder = Split-Path -Parent $ZoneExe
    $serverRoot = Split-Path -Parent $serverFolder
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $known = @(
        (Join-Path $serverFolder 'Zone.pdb'),
        (Join-Path $serverRoot 'Zone.pdb'),
        'C:\NextGenZone1669Capture\Symbols\Zone.pdb',
        'C:\NextGenSymbols\Zone.pdb',
        (Join-Path $repoRoot 'Zone.pdb')
    )
    foreach ($item in $known) {
        if (Test-Path -LiteralPath $item -PathType Leaf) {
            return (Resolve-Path -LiteralPath $item).Path
        }
    }
    foreach ($root in (@($serverRoot, $repoRoot, 'C:\NextGenServerTools') | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $first = Get-ChildItem -LiteralPath $root -Filter Zone.pdb -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $first) { return $first.FullName }
    }
    throw 'Zone.pdb fehlt. Bitte nach C:\NextGenZone1669Capture\Symbols\Zone.pdb kopieren oder -PdbPath verwenden.'
}

function Get-ExactZoneProcess {
    param([string]$ExpectedExe)
    $path = [IO.Path]::GetFullPath($ExpectedExe)
    @(Get-CimInstance Win32_Process -Filter "Name='Zone.exe'" -ErrorAction Stop |
        Where-Object {
            $_.ExecutablePath -and
            [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $path,
                [StringComparison]::OrdinalIgnoreCase)
        })
}

if ($SelfTest) {
    $output = @(& $preparer -TemplateSelfTest)
    if ($output -notcontains 'ZONE1669_CAPTURE_SELFTEST PASS') {
        throw 'One-shot Basis-Selbsttest fehlgeschlagen.'
    }
    if (-not (Get-Command Find-ZonePdb, Get-ExactZoneProcess -ErrorAction Stop)) {
        throw 'Zone01-Hilfsfunktionen fehlen.'
    }
    'ZONE1669_ZONE01_SELFTEST PASS'
    return
}

if (-not (Test-Path -LiteralPath $ZonePath -PathType Leaf)) {
    throw "Zone01 nicht gefunden: $ZonePath"
}
$exe = (Resolve-Path -LiteralPath $ZonePath).Path
Write-Host "[1/4] Zone01: $exe" -ForegroundColor Cyan
$pdb = Find-ZonePdb -ZoneExe $exe -Preferred $PdbPath
Write-Host "[2/4] Zone.pdb: $pdb" -ForegroundColor Cyan

$safeFolder = Join-Path $OutputDirectory 'Symbols'
if ($safeFolder -match '[\s";]' -or $safeFolder.Contains([char]96)) {
    throw 'OutputDirectory darf keine Leerzeichen, Anführungszeichen oder Semikolons enthalten.'
}
New-Item -ItemType Directory -Path $safeFolder -Force | Out-Null
$safePdb = Join-Path $safeFolder 'Zone.pdb'
if (-not [string]::Equals([IO.Path]::GetFullPath($pdb),
                          [IO.Path]::GetFullPath($safePdb),
                          [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $pdb -Destination $safePdb -Force
}
$lines = @(& $preparer -ZoneExePath $exe -ZonePdbPath $safePdb -OutputDirectory $OutputDirectory)
$commands = @($lines | Where-Object { $_ -match '^\s*\$\$>a<.*Zone1669_Arm\.wds$' })
if ($commands.Count -ne 1) { throw 'Kein eindeutiger WinDbg-Befehl; Abbruch.' }
$windbgCommand = $commands[0].Trim()
Write-Host '[3/4] EXE/PDB verifiziert, Einmal-Messpunkt vorbereitet.' -ForegroundColor Green
Write-Host ($lines | Select-Object -Last 4 | Out-String)

if ($PrepareOnly) {
    Write-Host 'Nur vorbereitet. WinDbg wurde nicht gestartet.'
    return
}
$instances = @(Get-ExactZoneProcess -ExpectedExe $exe)
if ($instances.Count -ne 1) {
    Write-Warning "Zone01-Prozess nicht eindeutig gefunden ($($instances.Count) Treffer)."
    Write-Host 'Bitte Zone01 isoliert starten und denselben Befehl erneut ausführen.'
    Write-Host $windbgCommand
    return
}
$zonePid = [int]$instances[0].ProcessId
Write-Host "[4/4] Zone01 Prozess PID $zonePid" -ForegroundColor Cyan
Write-Warning 'Debugger pausiert Zone01 kurz: nur spielerfreie TESTZONE verwenden!'
$answer = Read-Host 'Bestätige die isolierte Testzone mit TESTZONE01'
if ($answer -cne 'TESTZONE01') {
    Write-Host 'Keine Prozessverbindung hergestellt.'
    return
}

$pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$classicPath = if ($pf86) { Join-Path $pf86 'Windows Kits\10\Debuggers\x86\windbg.exe' } else { '' }
if ($classicPath -and (Test-Path -LiteralPath $classicPath -PathType Leaf)) {
    Write-Host "Starte klassisches WinDbg x86 mit Zone01 PID $zonePid ..."
    Start-Process -FilePath $classicPath -ArgumentList @(
        '-p', "$zonePid", '-c', ('"' + $windbgCommand + '"')) -ErrorAction Stop | Out-Null
    Write-Host 'Logdatei bei erstem Treffer: Zone1669_FirstHit_*.log'
    return
}

# Modern Store WinDbg: manual attach is safer than guessing unsupported CLI flags.
try {
    if (Get-Command Set-Clipboard -ErrorAction SilentlyContinue) {
        Set-Clipboard -Value $windbgCommand -ErrorAction Stop
        Write-Host 'WinDbg-Befehl in Zwischenablage kopiert.' -ForegroundColor Green
    }
} catch { Write-Warning 'Kopieren fehlgeschlagen. Befehl unten manuell verwenden.' }
Write-Host 'NEUES WinDbg (Store):'
Write-Host "  1. File → Attach to Process → Zone.exe, PID $zonePid"
Write-Host '  2. Im Debugger lmvm Zone prüfen (Pfad/Symbole).'
Write-Host "  3. Befehl mit Strg+V und Enter ausführen: $windbgCommand"
Write-Host '  4. Den ersten Trefferlog Zone1669_FirstHit_*.log hier hochladen.'
