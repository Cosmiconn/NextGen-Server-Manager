param(
    [Parameter(Mandatory = $true)]
    [string]$TargetZoneExe,

    [ValidateRange(0, 3600)]
    [int]$WaitSeconds = 120,

    [ValidateRange(0, 3600)]
    [int]$MinimumUptimeSeconds = 10,

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

Write-Host 'NextGen Zone Pool - Runtime-Verifikation 2000/12000/512'
Write-Host "Ziel: $targetPath"
Write-Host ''
Write-Host 'READ-ONLY: Dieses Script startet, stoppt oder veraendert keinen Prozess und keine Serverdatei.'
Write-Host 'PASS verlangt Deployment-State DEPLOYED, gepinnte Hashes, exakt einen Zone-Prozess aus diesem Pfad'
Write-Host 'und Runtime-Maxima Player 2000 / Mob 12000 / NPC 512 im ShineObjectManager.'
Write-Host ''

$assemblyDirectory = Split-Path -Parent $assemblyPath
Push-Location $assemblyDirectory
try {
    [void][System.Reflection.Assembly]::LoadFrom($assemblyPath)
    $type = [Type]::GetType(
        'NextGen.Fiesta.ServerManager.Services.ZonePoolRuntimeTestObserver, NextGen.Fiesta.ServerManager',
        $true)
    $observer = [Activator]::CreateInstance($type)
    $method = $type.GetMethod('Observe')

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $lastDetail = $null

    while ($true) {
        $result = $method.Invoke($observer, @($targetPath, $MinimumUptimeSeconds))

        if ($result.Detail -ne $lastDetail) {
            Write-Host "[$($result.Status)] $($result.Detail)"
            $lastDetail = $result.Detail
        }

        if ($result.Passed) {
            Write-Host ''
            Write-Host "PID:              $($result.ProcessId)"
            Write-Host "Process Start UTC: $($result.ProcessStartedUtc)"
            Write-Host "Target SHA256:     $($result.TargetSha256)"
            Write-Host "Backup SHA256:     $($result.BackupSha256)"
            if ($null -ne $result.Pools) {
                Write-Host "Binary Profile:    $($result.Pools.BinaryProfile)"
                Write-Host "Player:            $($result.Pools.PlayerCount) / $($result.Pools.PlayerLimit)"
                Write-Host "Mob:               $($result.Pools.MobCount) / $($result.Pools.MobLimit)"
                Write-Host "NPC:               $($result.Pools.NpcCount) / $($result.Pools.NpcLimit)"
            }
            Write-Host ''
            Write-Host 'RUNTIME TEST: PASS'
            exit 0
        }

        if ($result.Blocked) {
            Write-Error 'RUNTIME TEST: BLOCKED'
            exit 20
        }

        if (-not $result.Waiting) {
            Write-Error 'RUNTIME TEST: unbekannter Observer-Zustand'
            exit 21
        }

        if ((Get-Date) -ge $deadline) {
            Write-Error "RUNTIME TEST: TIMEOUT nach $WaitSeconds Sekunden. Letzter Zustand: $($result.Detail)"
            exit 22
        }

        Start-Sleep -Seconds 2
    }
}
finally {
    Pop-Location
}
