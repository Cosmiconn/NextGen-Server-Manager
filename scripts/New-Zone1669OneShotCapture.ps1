#requires -Version 5.1
<#
.SYNOPSIS
    Prepare a ONE-HIT WinDbg (x86) capture for NA2016 Zone Disconnect(1669).
.DESCRIPTION
    No server process is attached, launched, stopped, or patched by this script.
    It verifies the EXACT supplied original Zone.exe (SHA-256 and PE RSDS GUID/Age)
    and writes two WinDbg command scripts. The operator must explicitly attach
    a debugger to a disposable TEST Zone and arm the one-shot breakpoint.
    The breakpoint is on the failure-only branch at Zone+0x0005AE5A, BEFORE
    eax is overwritten and BEFORE Disconnect(1669) is called.
.PARAMETER ZoneExePath
    Full path of the exact unmodified NA2016 Zone.exe (not a hooked copy).
.PARAMETER ZonePdbPath
    Full path of the matching original Zone.pdb.
.PARAMETER OutputDirectory
    Destination for generated debugger scripts and the first-hit trace.
.PARAMETER TemplateSelfTest
    Validate the debugger script templates without reading any Zone binaries.
#>
[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Prepare')]
    [string]$ZoneExePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Prepare')]
    [string]$ZonePdbPath,
    [Parameter(ParameterSetName = 'Prepare')]
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'NextGenFiestaServerManager\Zone1669Capture'),
    [Parameter(Mandatory = $true, ParameterSetName = 'Test')]
    [switch]$TemplateSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedSha256 = 'db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5'
$expectedGuid = [Guid]'339124ac-7257-466d-ac43-793bb53692ab'
$expectedAge = 16

# 0x0005AE56 test eax,eax; 0x0005AE58 je success; 0x0005AE5A failure:
# mov eax,[esi], followed by the virtual Disconnect(0x685,0,0).
# Inside this branch eax is STILL the fm_Marking return value.
$failureRva = '0x0005AE5A'

$onHitTemplate = @'
.logopen /t __LOGPATH__
.echo ===== ZONE1669 FIRST FAILURE HIT =====
.time
lmvm Zone
.echo ===== RETURN CODE AND REGISTER STATE =====
r
.printf "FM_MARKING_RETURN=%x SHINE_OBJECT=%p\n", @eax, @esi
.echo ===== CANDIDATE POSITION AND FIELDMAP BOUNDS =====
.printf "XY_POINTER=%p FIELDMAP_POINTER=%p\n", poi(@esi+0x66), poi(@esi+0x7a)
.printf "CANDIDATE_X=%u CANDIDATE_Y=%u MAP_X_BOUND=%u MAP_Y_BOUND=%u\n", poi(poi(@esi+0x66)), poi(poi(@esi+0x66)+4), poi(poi(@esi+0x7a)+0x18), poi(poi(@esi+0x7a)+0x1c)
.echo ===== RAW XY AND MAP BOUNDS (TWO DWORDS EACH) =====
dd poi(@esi+0x66) L2
dd poi(@esi+0x7a)+0x18 L2
.echo ===== SHADOWSTEP LOCAL REFERENCE XY (MEANING NOT YET VERIFIED) =====
dd @ebp-0x24 L2
.echo ===== CALL STACK: SKILL / ACTOR EVIDENCE =====
kv 20
.echo ===== END OF ONE-SHOT CAPTURE: RESUMING TEST ZONE =====
.logclose
gc
'@

$armTemplate = @'
.echo ===== NA2016 ZONE1669 ONE-SHOT SETUP =====
.sympath+ __PDBDIR__
.reload /f Zone.exe
lmvm Zone
.echo Verify Zone image and PDB are loaded before continuing.
.echo The next breakpoint is on the 1669 FAILURE BRANCH ONLY, not on all fm_Marking calls.
u Zone+0x0005AE56 L8
bp /1 Zone+0x0005AE5A "$$>a<__HITPATH__"
bl
g
'@

if ($TemplateSelfTest) {
    if (-not $armTemplate.Contains('bp /1 Zone+0x0005AE5A')) {
        throw 'Missing failure-only one-shot breakpoint.'
    }
    if ($armTemplate.Contains('bp Zone+0x0005AE56')) {
        throw 'Hot-path breakpoint not permitted.'
    }
    if (-not $onHitTemplate.Contains('FM_MARKING_RETURN=%x') -or
        -not $onHitTemplate.Contains('poi(@esi+0x66)') -or
        -not $onHitTemplate.Contains('poi(@esi+0x7a)+0x18') -or
        -not $onHitTemplate.Contains('kv 20') -or
        -not $onHitTemplate.TrimEnd().EndsWith('gc')) {
        throw 'Missing required bounded evidence collection or resume command.'
    }
    Write-Output 'ZONE1669_CAPTURE_SELFTEST PASS'
    return
}

$exe = (Resolve-Path -LiteralPath $ZoneExePath).Path
$pdb = (Resolve-Path -LiteralPath $ZonePdbPath).Path
if (-not [string]::Equals([IO.Path]::GetFileName($exe), 'Zone.exe',
                         [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Only an original file named Zone.exe is supported.'
}
if (-not [string]::Equals([IO.Path]::GetFileName($pdb), 'Zone.pdb',
                         [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Supply the original matching Zone.pdb, not a renamed alternative.'
}
$actualHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedSha256) {
    throw "Zone.exe SHA-256 mismatch. Expected $expectedSha256; observed $actualHash. No scripts written."
}

# Inspect the PDB lookup identity embedded in the original PE's RSDS record.
# The SHA-256 check above prevents incorrectly accepting an unrelated lookalike PE.
$bytes = [IO.File]::ReadAllBytes($exe)
$binaryView = [Text.Encoding]::ASCII.GetString($bytes)
$found = $false
$i = $binaryView.IndexOf('RSDS', [StringComparison]::Ordinal)
while ($i -ge 0 -and $i -le $bytes.Length - 24) {
    $guidBytes = New-Object byte[] 16
    [Array]::Copy($bytes, $i + 4, $guidBytes, 0, 16)
    $guid = [Guid]::new([byte[]]$guidBytes)
    $age = [BitConverter]::ToUInt32($bytes, $i + 20)
    if ($guid -eq $expectedGuid -and $age -eq $expectedAge) {
        $found = $true
        break
    }
    $i = $binaryView.IndexOf('RSDS', $i + 4, [StringComparison]::Ordinal)
}
if (-not $found) {
    throw "PE RSDS debug identity mismatch: expected GUID $expectedGuid / age $expectedAge. No scripts written."
}

# Independently verify GUID and Age stored in PDB MSF7 stream #1 (PDB Info).
# Merely naming an unrelated file Zone.pdb is not sufficient.
$pdbBytes = [IO.File]::ReadAllBytes($pdb)
if ($pdbBytes.Length -lt 56 -or
    -not [Text.Encoding]::ASCII.GetString($pdbBytes, 0, 24).StartsWith('Microsoft C/C++ MSF 7.00')) {
    throw 'Zone.pdb is not a valid MSF 7.0 PDB; no scripts written.'
}
$blockSize = [BitConverter]::ToUInt32($pdbBytes, 32)
$directoryBytes = [BitConverter]::ToUInt32($pdbBytes, 44)
$blockMapAddress = [BitConverter]::ToUInt32($pdbBytes, 52)
if ($blockSize -lt 512 -or $blockSize -gt 4096 -or
    $directoryBytes -lt 32 -or $directoryBytes -gt $pdbBytes.Length -or
    $blockMapAddress * [long]$blockSize -ge $pdbBytes.Length) {
    throw 'Zone.pdb has invalid MSF directory metadata; no scripts written.'
}
$directoryBlockCount = [int][Math]::Ceiling($directoryBytes / [double]$blockSize)
if ($directoryBlockCount -gt [int]($blockSize / 4)) {
    throw 'Zone.pdb uses an unsupported oversized directory map; no scripts written.'
}
$directory = New-Object byte[] ([int]$directoryBytes)
for ($b = 0; $b -lt $directoryBlockCount; $b++) {
    $blockIdOffset = [int]($blockMapAddress * [long]$blockSize + $b * 4)
    $blockId = [BitConverter]::ToUInt32($pdbBytes, $blockIdOffset)
    $source = $blockId * [long]$blockSize
    $length = [int][Math]::Min($blockSize, $directoryBytes - $b * $blockSize)
    if ($source + $length -gt $pdbBytes.Length) {
        throw 'Zone.pdb directory block is out of bounds; no scripts written.'
    }
    [Array]::Copy($pdbBytes, $source, $directory, $b * [long]$blockSize, $length)
}
$streamCount = [BitConverter]::ToUInt32($directory, 0)
if ($streamCount -lt 2 -or (4 + $streamCount * 4) -ge $directory.Length) {
    throw 'Zone.pdb stream directory is invalid; no scripts written.'
}
$stream0Size = [BitConverter]::ToUInt32($directory, 4)
$stream1Size = [BitConverter]::ToUInt32($directory, 8)
if ($stream0Size -eq [uint32]::MaxValue -or $stream1Size -lt 28 -or
    $stream1Size -eq [uint32]::MaxValue) {
    throw 'Zone.pdb Info stream is missing; no scripts written.'
}
$stream0Blocks = [int][Math]::Ceiling($stream0Size / [double]$blockSize)
$stream1BlockOffset = [int](4 + $streamCount * 4 + $stream0Blocks * 4)
if ($stream1BlockOffset + 4 -gt $directory.Length) {
    throw 'Zone.pdb Info stream block pointer is invalid; no scripts written.'
}
$stream1Block = [BitConverter]::ToUInt32($directory, $stream1BlockOffset)
$infoOffset = [int]($stream1Block * [long]$blockSize)
if ($infoOffset + 28 -gt $pdbBytes.Length) {
    throw 'Zone.pdb Info stream block lies outside the file; no scripts written.'
}
$pdbAge = [BitConverter]::ToUInt32($pdbBytes, $infoOffset + 8)
$pdbGuidBytes = New-Object byte[] 16
[Array]::Copy($pdbBytes, $infoOffset + 12, $pdbGuidBytes, 0, 16)
$pdbGuid = [Guid]::new([byte[]]$pdbGuidBytes)
if ($pdbAge -ne $expectedAge -or $pdbGuid -ne $expectedGuid) {
    throw "Zone.pdb GUID/Age mismatch: expected $expectedGuid/$expectedAge, observed $pdbGuid/$pdbAge. No scripts written."
}

$root = [IO.Path]::GetFullPath($OutputDirectory)
# WinDbg script invocation and bp command strings have sensitive punctuation rules.
# Require a plain path to make the copy/paste debugger commands unambiguous.
if ($root -match '[\s";]' -or $root.Contains([char]96)) {
    throw 'Please choose a Windows OutputDirectory path without spaces, quotes, semicolons or backticks.'
}
$pdbDir = [IO.Path]::GetDirectoryName($pdb)
if ($pdbDir -match '[\s";]' -or $pdbDir.Contains([char]96)) {
    throw 'Please put the PDB in a directory without spaces, quotes, semicolons or backticks.'
}
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$folder = Join-Path $root ('run-' + $runId)
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$hitPath = Join-Path $folder 'Zone1669_Hit.wds'
$armPath = Join-Path $folder 'Zone1669_Arm.wds'
$logPath = Join-Path $folder 'Zone1669_FirstHit.log'
$hit = $onHitTemplate.Replace('__LOGPATH__', $logPath)
$arm = $armTemplate.Replace('__PDBDIR__', $pdbDir).Replace('__HITPATH__', $hitPath)
# ASCII preserves the exact x86 debugger commands, and avoids adding a UTF-8 BOM.
[IO.File]::WriteAllText($hitPath, $hit + [Environment]::NewLine,
    [Text.Encoding]::ASCII)
[IO.File]::WriteAllText($armPath, $arm + [Environment]::NewLine,
    [Text.Encoding]::ASCII)
Write-Output 'ZONE1669 CAPTURE PREPARED - no process has been attached or modified.'
Write-Output "Zone.exe SHA256: $actualHash"
Write-Output "Zone.exe RSDS GUID/Age: $expectedGuid / $expectedAge"
Write-Output "PDB verified (GUID/Age = original; check live symbol load in WinDbg): $pdb"
Write-Output "Failure-only breakpoint: Zone+$failureRva (one-shot)"
Write-Output "WinDbg (x86) command to run AFTER attaching to the ISOLATED TEST Zone.exe:"
Write-Output ('  $$>a<' + $armPath)
Write-Output "Expected first-hit log: $logPath (WinDbg .logopen /t appends PID/time)"
Write-Output 'Use lmvm Zone to verify the loaded image/PDB; do NOT attach to the primary benchmark Zone.'
Write-Output 'The one-shot breakpoint will auto-resume. Captures call stack and runtime addresses; review before sharing.'
