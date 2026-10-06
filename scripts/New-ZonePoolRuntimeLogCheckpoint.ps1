param(
    [Parameter(Mandatory = $true)]
    [string]$ServerRoot,

    [Parameter(Mandatory = $true)]
    [string]$Checkpoint,

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
$checkpointFull = [IO.Path]::GetFullPath($Checkpoint)
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool - Runtime Log Checkpoint'
Write-Host "Server Root: $rootPath"
Write-Host "Checkpoint:  $checkpointFull"
Write-Host 'Der Server-Root wird ausschließlich gelesen. Der Checkpoint muss außerhalb des Server-Roots liegen.'
Write-Host ''

Push-Location (Split-Path -Parent $assemblyPath)
try {
    [void][Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType('NextGen.Fiesta.ServerManager.Services.ZonePoolRuntimeLogDeltaAudit, NextGen.Fiesta.ServerManager', $true)
    $audit = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('SaveCheckpoint').Invoke($audit, @($rootPath, $checkpointFull))
    Write-Host $result.Detail
    if (-not $result.Success) { Write-Error 'LOG CHECKPOINT: FAILURE'; exit 20 }
    Write-Host "Zeit UTC: $($result.CreatedUtc)"
    Write-Host "Dateien:  $($result.FileCount)"
    Write-Host 'LOG CHECKPOINT: SUCCESS'
    exit 0
}
finally { Pop-Location }
