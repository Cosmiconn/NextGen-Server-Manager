param(
    [Parameter(Mandatory = $true)]
    [string]$TargetZoneExe,

    [Parameter(Mandatory = $true)]
    [string]$PatchedZoneExe,

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
$patchedPath = (Resolve-Path -LiteralPath $PatchedZoneExe).Path
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool - EIN-ZONEN TESTDEPLOYMENT'
Write-Host "Ziel:       $targetPath"
Write-Host "Patchkopie: $patchedPath"
Write-Host ''
Write-Host 'SICHERHEIT:'
Write-Host '- nur das zertifizierte Profil 2000 Player / 12000 Mob / 512 NPC wird akzeptiert'
Write-Host '- kein Zone-Prozess darf laufen'
Write-Host '- Ziel muss exakt die verifizierte NA2016-Baseline sein'
Write-Host '- Austausch erfolgt atomar mit verpflichtendem Baseline-Backup'
Write-Host '- dieser Befehl startet KEINEN Serverprozess'
Write-Host ''

$assemblyDirectory = Split-Path -Parent $assemblyPath
Push-Location $assemblyDirectory
try {
    [void][System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType(
        'NextGen.Fiesta.ServerManager.Services.ZonePoolSingleZoneTestDeployment, NextGen.Fiesta.ServerManager',
        $true)
    $deployment = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('Deploy').Invoke($deployment, @($targetPath, $patchedPath, $Confirm))

    Write-Host $result.Detail
    if (-not [string]::IsNullOrWhiteSpace($result.TargetSha256)) { Write-Host "Target SHA256:  $($result.TargetSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.BackupSha256)) { Write-Host "Backup SHA256:  $($result.BackupSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.BackupPath)) { Write-Host "Backup:         $($result.BackupPath)" }
    if (-not [string]::IsNullOrWhiteSpace($result.DeploymentRecordPath)) { Write-Host "Deployment Log: $($result.DeploymentRecordPath)" }

    if (-not $result.Success) {
        if ($result.EmergencyRollbackAttempted) {
            Write-Host "Notfall-Rollback versucht: $($result.EmergencyRollbackSucceeded)"
        }
        Write-Error 'TESTDEPLOYMENT: FAILURE'
        exit 20
    }

    Write-Host ''
    Write-Host 'TESTDEPLOYMENT: SUCCESS'
    Write-Host 'Noch wurde KEINE Zone gestartet. Vor dem Start Ziel-SHA und Deployment-JSON dokumentieren.'
    exit 0
}
finally {
    Pop-Location
}
