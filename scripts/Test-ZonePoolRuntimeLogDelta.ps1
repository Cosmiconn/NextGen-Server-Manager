param(
    [Parameter(Mandatory = $true)]
    [string]$ServerRoot,

    [Parameter(Mandatory = $true)]
    [string]$Checkpoint,

    [ValidateRange(65536, 67108864)]
    [int]$MaxDeltaBytesPerFile = 4194304,

    [string]$ManagerAssembly
)

$ErrorActionPreference = 'Stop'

function Resolve-ManagerAssembly {
    param([string]$ExplicitPath)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) { return (Resolve-Path -LiteralPath $ExplicitPath).Path }
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $repoRoot 'src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\win-x64\publish\NextGen.Fiesta.ServerManager.dll'),
        (Join-Path $repoRoot 'src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\NextGen.Fiesta.ServerManager.dll'),
        (Join-Path $repoRoot 'publish\NextGen.Fiesta.ServerManager.dll')
    )
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path } }
    throw 'NextGen.Fiesta.ServerManager.dll wurde nicht gefunden. Release bauen oder -ManagerAssembly angeben.'
}

$rootPath = (Resolve-Path -LiteralPath $ServerRoot).Path
$checkpointPath = (Resolve-Path -LiteralPath $Checkpoint).Path
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool - Runtime Log Delta Audit'
Write-Host "Server Root: $rootPath"
Write-Host "Checkpoint:  $checkpointPath"
Write-Host 'READ-ONLY fuer alle Serverlogs: analysiert nur seit dem Checkpoint neue/angehaengte Logdaten.'
Write-Host ''

Push-Location (Split-Path -Parent $assemblyPath)
try {
    [void][Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType('NextGen.Fiesta.ServerManager.Services.ZonePoolRuntimeLogDeltaAudit, NextGen.Fiesta.ServerManager', $true)
    $audit = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('Audit').Invoke($audit, @($rootPath, $checkpointPath, $MaxDeltaBytesPerFile))

    Write-Host "[$($result.Status)] $($result.Detail)"
    Write-Host "Geaenderte Logs:       $($result.ChangedFileCount)"
    Write-Host "Neue Logs:             $($result.NewFileCount)"
    Write-Host "Rotiert/gekuerzt:      $($result.RotatedOrTruncatedFileCount)"
    Write-Host "Neue Bytes:            $($result.TotalDeltaBytes)"
    Write-Host "Evidenz vollstaendig:  $($result.EvidenceComplete)"

    if ($result.EvidenceGaps.Count -gt 0) {
        Write-Host ''
        Write-Host 'Evidenzluecken:'
        foreach ($gap in $result.EvidenceGaps) { Write-Host "  - $gap" }
    }

    if ($result.Findings.Count -gt 0) {
        Write-Host ''
        Write-Host 'Neue Befunde seit Checkpoint:'
        foreach ($finding in $result.Findings) {
            $marker = if ($finding.Blocking) { 'BLOCK' } else { 'REVIEW' }
            Write-Host "  [$marker][$($finding.Severity)][$($finding.Code)] $($finding.Title)"
            Write-Host "    Quelle: $($finding.Source)"
            Write-Host "    Evidenz: $($finding.Evidence)"
        }
    }

    if (-not $result.Success -or $result.Blocked) {
        Write-Error 'LOG DELTA AUDIT: BLOCKED'
        exit 20
    }
    if ($result.ReviewRequired) {
        Write-Warning 'LOG DELTA AUDIT: REVIEW - keine harte Patch-Crash-Signatur, aber Befunde/Evidenzluecken muessen manuell bewertet werden.'
        exit 10
    }

    Write-Host ''
    Write-Host 'LOG DELTA AUDIT: CLEAN'
    exit 0
}
finally { Pop-Location }
