param(
    [Parameter(Mandatory = $true)]
    [string]$ZoneExe,

    [string]$Output,

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

    throw 'NextGen.Fiesta.ServerManager.dll wurde nicht gefunden. Release bauen oder -ManagerAssembly angeben.'
}

$zonePath = (Resolve-Path -LiteralPath $ZoneExe).Path
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

if ([string]::IsNullOrWhiteSpace($Output)) {
    $directory = Split-Path -Parent $zonePath
    $Output = Join-Path $directory 'Zone.NextGen-2000-12000-512.exe'
}
$outputPath = [System.IO.Path]::GetFullPath($Output)

Write-Host 'NextGen Zone Pool Offline Copy'
Write-Host 'Zertifiziertes Profil: Player 2000 | Mob 12000 | NPC 512'
Write-Host "Baseline: $zonePath"
Write-Host "Output:   $outputPath"
Write-Host ''
Write-Host 'SICHERHEIT:'
Write-Host '- Originaldatei wird niemals in-place veraendert.'
Write-Host '- Existierende Ausgabedateien werden nicht ueberschrieben.'
Write-Host '- Jeder laufende Zone-Prozess blockiert den Vorgang.'
Write-Host '- Baseline-Hash, 83 Patch-Sites, Zielhash und Rollback werden geprueft.'
Write-Host '- Live-/In-Place-Patching bleibt gesperrt.'
Write-Host ''

$assemblyDirectory = Split-Path -Parent $assemblyPath
Push-Location $assemblyDirectory
try {
    [void][System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType(
        'NextGen.Fiesta.ServerManager.Services.ZonePoolOfflinePatchWriter, NextGen.Fiesta.ServerManager',
        $true)
    $writer = [Activator]::CreateInstance($type)
    $result = $type.GetMethod('CreatePatchedCopy').Invoke(
        $writer,
        @($zonePath, $outputPath, 2000, 12000, 512))

    Write-Host $result.Detail

    if (-not $result.Success) {
        Write-Error 'OFFLINE COPY: FAILURE'
        exit 20
    }

    if ($result.SafetyGate.CanCreateOfflinePatchedCopy -ne $true) {
        throw 'Writer meldete Erfolg ohne CanCreateOfflinePatchedCopy.'
    }
    if ($result.SafetyGate.OfflineWriterCertified -ne $true) {
        throw 'Writer meldete Erfolg ohne OfflineWriterCertified.'
    }
    if ($result.SafetyGate.CanWriteBinary -ne $false) {
        throw 'Live-/In-Place-Binaerschreiben wurde unerwartet freigegeben.'
    }

    Write-Host "Baseline SHA256: $($result.BaselineSha256)"
    Write-Host "Patched  SHA256: $($result.PatchedSha256)"
    Write-Host "Sites geaendert:  $($result.ChangedSiteCount)"
    Write-Host "Backup:           $($result.BackupPath)"
    Write-Host "Manifest:         $($result.MetadataPath)"
    Write-Host ''
    Write-Host 'OFFLINE COPY: SUCCESS'
    Write-Host 'Die erzeugte Datei wurde NICHT installiert oder gestartet.'
    exit 0
}
finally {
    Pop-Location
}
