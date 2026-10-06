param(
    [Parameter(Mandatory = $true)]
    [string]$TargetZoneExe,

    [Parameter(Mandatory = $true)]
    [string]$Confirm,

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
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool - EIN-ZONEN TESTROLLBACK'
Write-Host "Ziel: $targetPath"
Write-Host ''
Write-Host 'Der Rollback wird nur akzeptiert, wenn Ziel, Backup und Deployment-JSON exakt zu dem registrierten Testdeployment passen.'
Write-Host 'Kein Zone-Prozess darf laufen. Der gepatchte Stand wird als Auditbackup erhalten.'
Write-Host ''

$assemblyDirectory = Split-Path -Parent $assemblyPath
Push-Location $assemblyDirectory
try {
    [void][System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType(
        'NextGen.Fiesta.ServerManager.Services.ZonePoolSingleZoneTestDeployment, NextGen.Fiesta.ServerManager',
        $true)
    $deployment = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('Rollback').Invoke($deployment, @($targetPath, $Confirm))

    Write-Host $result.Detail
    if (-not [string]::IsNullOrWhiteSpace($result.TargetSha256)) { Write-Host "Target SHA256:       $($result.TargetSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.BackupSha256)) { Write-Host "Baseline Backup SHA: $($result.BackupSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.RollbackPatchedBackupPath)) { Write-Host "Patch Auditbackup:   $($result.RollbackPatchedBackupPath)" }
    if (-not [string]::IsNullOrWhiteSpace($result.DeploymentRecordPath)) { Write-Host "Deployment Log:      $($result.DeploymentRecordPath)" }

    if (-not $result.Success) {
        Write-Error 'TESTROLLBACK: FAILURE'
        exit 20
    }

    Write-Host ''
    Write-Host 'TESTROLLBACK: SUCCESS'
    Write-Host 'Ziel-Zone.exe ist wieder die verifizierte NA2016-Baseline. Es wurde KEIN Zone-Prozess gestartet.'
    exit 0
}
finally {
    Pop-Location
}
