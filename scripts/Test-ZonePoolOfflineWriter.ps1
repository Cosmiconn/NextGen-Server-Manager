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
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw @"
NextGen.Fiesta.ServerManager.dll wurde nicht gefunden.
Baue/publiziere zuerst Release oder uebergib -ManagerAssembly explizit.
Beispiel:
  .\scripts\Test-ZonePoolOfflineWriter.ps1 -ZoneExe 'C:\NextGenFiestaServer\Zone00\Zone.exe' -ManagerAssembly 'C:\NextGenServerManager\NextGen.Fiesta.ServerManager.dll'
"@
}

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Dieses .NET-8-Testscript benoetigt PowerShell 7 oder neuer (pwsh). Aktuell: PowerShell $($PSVersionTable.PSVersion). Starte 'pwsh' und fuehre den Befehl dort erneut aus."
}

$zonePath = (Resolve-Path -LiteralPath $ZoneExe).Path
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool Offline Writer - isolierter Selbsttest'
Write-Host "Zone.exe: $zonePath"
Write-Host "Manager:  $assemblyPath"
Write-Host "PowerShell: $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
Write-Host ''
Write-Host 'WICHTIG: Der Test verweigert die Ausfuehrung, sobald ein Zone-Prozess laeuft.'
Write-Host 'Die originale Zone.exe wird niemals ueberschrieben; gearbeitet wird nur in einem temporaeren Verzeichnis.'
Write-Host ''

$assemblyDirectory = Split-Path -Parent $assemblyPath
Push-Location $assemblyDirectory
try {
    $assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = $assembly.GetType(
        'NextGen.Fiesta.ServerManager.Services.ZonePoolOfflineWriterSelfTest',
        $true,
        $false)
    $test = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('Run').Invoke($test, @($zonePath))

    Write-Host $result.Detail
    if (-not [string]::IsNullOrWhiteSpace($result.BaselineSha256)) {
        Write-Host "Baseline SHA256: $($result.BaselineSha256)"
    }
    if (-not [string]::IsNullOrWhiteSpace($result.PatchedSha256)) {
        Write-Host "Patched  SHA256: $($result.PatchedSha256)"
    }
    if (-not [string]::IsNullOrWhiteSpace($result.RollbackSha256)) {
        Write-Host "Rollback SHA256: $($result.RollbackSha256)"
    }

    if ($result.Success) {
        Write-Host ''
        Write-Host 'SELFTEST: SUCCESS'
        exit 0
    }

    Write-Error 'SELFTEST: FAILURE'
    exit 20
}
finally {
    Pop-Location
}
