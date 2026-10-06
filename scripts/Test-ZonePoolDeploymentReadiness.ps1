param(
    [Parameter(Mandatory = $true)]
    [string]$TargetZoneExe,

    [Parameter(Mandatory = $true)]
    [string]$PatchedZoneExe,

    [string]$ManagerAssembly
)

$ErrorActionPreference = 'Stop'

function Resolve-ManagerAssembly {
    param([string]$ExplicitPath)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $repoRoot 'src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\win-x64\publish\NextGen.Fiesta.ServerManager.dll'),
        (Join-Path $repoRoot 'src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\NextGen.Fiesta.ServerManager.dll'),
        (Join-Path $repoRoot 'publish\NextGen.Fiesta.ServerManager.dll')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw 'NextGen.Fiesta.ServerManager.dll wurde nicht gefunden. Release bauen oder -ManagerAssembly angeben.'
}

$targetPath = (Resolve-Path -LiteralPath $TargetZoneExe).Path
$patchedPath = (Resolve-Path -LiteralPath $PatchedZoneExe).Path
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool Deployment Preflight (READ ONLY)'
Write-Host "Ziel:       $targetPath"
Write-Host "Patchkopie: $patchedPath"
Write-Host 'Dieser Befehl nimmt keinerlei Dateiaenderungen vor.'
Write-Host ''

$assemblyDirectory = Split-Path -Parent $assemblyPath
Push-Location $assemblyDirectory
try {
    [void][System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType(
        'NextGen.Fiesta.ServerManager.Services.ZonePoolDeploymentPreflight, NextGen.Fiesta.ServerManager',
        $true)
    $preflight = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('Analyze').Invoke($preflight, @($targetPath, $patchedPath))

    Write-Host $result.Detail
    if (-not [string]::IsNullOrWhiteSpace($result.TargetBaselineSha256)) { Write-Host "Target Baseline SHA256: $($result.TargetBaselineSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.PatchedSha256)) { Write-Host "Patched SHA256:         $($result.PatchedSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.PlannedTargetBackupPath)) { Write-Host "Geplantes Backup:       $($result.PlannedTargetBackupPath)" }
    if (-not [string]::IsNullOrWhiteSpace($result.PlannedDeploymentRecordPath)) { Write-Host "Geplante Metadaten:     $($result.PlannedDeploymentRecordPath)" }

    if (-not $result.Ready) {
        Write-Error 'DEPLOYMENT PREFLIGHT: BLOCKED'
        exit 20
    }

    Write-Host ''
    Write-Host 'DEPLOYMENT PREFLIGHT: READY'
    Write-Host 'Noch wurde KEINE Zone.exe ersetzt oder gestartet.'
    exit 0
}
finally {
    Pop-Location
}
