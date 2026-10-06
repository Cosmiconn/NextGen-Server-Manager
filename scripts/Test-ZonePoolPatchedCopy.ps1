param(
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

$patchedPath = (Resolve-Path -LiteralPath $PatchedZoneExe).Path
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool Patched Copy - Read-only Verifier'
Write-Host "Patchkopie: $patchedPath"
Write-Host 'Es werden keine Dateien veraendert.'
Write-Host ''

$assemblyDirectory = Split-Path -Parent $assemblyPath
Push-Location $assemblyDirectory
try {
    [void][System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType(
        'NextGen.Fiesta.ServerManager.Services.ZonePoolPatchedCopyVerifier, NextGen.Fiesta.ServerManager',
        $true)
    $verifier = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('Verify').Invoke($verifier, @($patchedPath))

    Write-Host $result.Detail
    if (-not [string]::IsNullOrWhiteSpace($result.BaselineSha256)) { Write-Host "Baseline SHA256: $($result.BaselineSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.PatchedSha256))  { Write-Host "Patched  SHA256: $($result.PatchedSha256)" }
    if (-not [string]::IsNullOrWhiteSpace($result.RollbackSha256)) { Write-Host "Rollback SHA256: $($result.RollbackSha256)" }
    Write-Host "Verifizierte geaenderte Sites: $($result.VerifiedSiteCount)"

    if (-not $result.Success) {
        Write-Error 'PATCH COPY VERIFY: FAILURE'
        exit 20
    }
    if ($result.SafetyGate.CanWriteBinary -ne $false) {
        throw 'Live-/In-Place-Binaerschreiben wurde unerwartet freigegeben.'
    }

    Write-Host ''
    Write-Host 'PATCH COPY VERIFY: SUCCESS'
    exit 0
}
finally {
    Pop-Location
}
