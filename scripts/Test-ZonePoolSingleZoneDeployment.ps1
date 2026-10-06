param(
    [Parameter(Mandatory = $true)]
    [string]$ZoneExe,

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

$zonePath = (Resolve-Path -LiteralPath $ZoneExe).Path
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool - isolierter Deployment/Rollback End-to-End-Selbsttest'
Write-Host "Baseline: $zonePath"
Write-Host ''
Write-Host 'WICHTIG:'
Write-Host '- kein Zone-Prozess darf laufen'
Write-Host '- die angegebene Zone.exe wird nur gelesen und niemals ersetzt'
Write-Host '- alle Schreibvorgaenge finden in einem frischen TEMP-Verzeichnis statt'
Write-Host '- der Test erzeugt Patchkopie, deployt auf eine TEMP-Zielkopie und rollt diese wieder zurueck'
Write-Host ''

$assemblyDirectory = Split-Path -Parent $assemblyPath
Push-Location $assemblyDirectory
try {
    [void][System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType(
        'NextGen.Fiesta.ServerManager.Services.ZonePoolSingleZoneDeploymentSelfTest, NextGen.Fiesta.ServerManager',
        $true)
    $test = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('Run').Invoke($test, @($zonePath))

    Write-Host $result.Detail
    if (-not [string]::IsNullOrWhiteSpace($result.BaselineSha256)) { Write-Host "Baseline SHA256:    $($result.BaselineSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.DeployedSha256)) { Write-Host "Deployed SHA256:    $($result.DeployedSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.RolledBackSha256)) { Write-Host "Rolled-back SHA256: $($result.RolledBackSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.TemporaryDirectory)) { Write-Host "TEMP:               $($result.TemporaryDirectory)" }

    if (-not $result.Success) {
        Write-Error 'DEPLOYMENT SELFTEST: FAILURE'
        exit 20
    }

    Write-Host ''
    Write-Host 'DEPLOYMENT SELFTEST: SUCCESS'
    Write-Host 'Der echte Serverordner wurde nicht veraendert.'
    exit 0
}
finally {
    Pop-Location
}
