param(
    [Parameter(Mandatory = $true)]
    [string]$TargetZoneExe,

    [ValidateRange(10, 7200)]
    [int]$DurationSeconds = 300,

    [ValidateRange(2, 60)]
    [int]$SampleIntervalSeconds = 5,

    [ValidateRange(0, 3600)]
    [int]$InitialWaitSeconds = 120,

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

function Invoke-Observation {
    param($Method, $Observer, [string]$Target, [int]$MinimumUptime)
    return $Method.Invoke($Observer, @($Target, $MinimumUptime))
}

$targetPath = (Resolve-Path -LiteralPath $TargetZoneExe).Path
$assemblyPath = Resolve-ManagerAssembly -ExplicitPath $ManagerAssembly

Write-Host 'NextGen Zone Pool - Runtime-Stabilitaetswache 2000/12000/512'
Write-Host "Ziel:       $targetPath"
Write-Host "Dauer:      $DurationSeconds s"
Write-Host "Intervall:  $SampleIntervalSeconds s"
Write-Host ''
Write-Host 'READ-ONLY: Kein Start, Stop oder Schreibzugriff.'
Write-Host 'Nach dem ersten PASS werden PID, Prozessstart, Binary-Profil und Pool-Maxima fest gepinnt.'
Write-Host 'Jeder Prozesswechsel, Neustart, WAIT/BLOCKED-Sample oder Profilwechsel beendet den Test als FAILURE.'
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

    $initialDeadline = (Get-Date).AddSeconds($InitialWaitSeconds)
    $baseline = $null
    $lastDetail = $null

    while ($null -eq $baseline) {
        $result = Invoke-Observation -Method $method -Observer $observer -Target $targetPath -MinimumUptime $MinimumUptimeSeconds
        if ($result.Detail -ne $lastDetail) {
            Write-Host "[$($result.Status)] $($result.Detail)"
            $lastDetail = $result.Detail
        }

        if ($result.Blocked) {
            Write-Error 'STABILITY TEST: BLOCKED vor Start des Beobachtungsfensters.'
            exit 20
        }
        if ($result.Passed) {
            $baseline = $result
            break
        }
        if (-not $result.Waiting) {
            Write-Error 'STABILITY TEST: unbekannter Observer-Zustand.'
            exit 21
        }
        if ((Get-Date) -ge $initialDeadline) {
            Write-Error "STABILITY TEST: INITIAL TIMEOUT nach $InitialWaitSeconds Sekunden. Letzter Zustand: $($result.Detail)"
            exit 22
        }
        Start-Sleep -Seconds 2
    }

    if ($null -eq $baseline.Pools) {
        Write-Error 'STABILITY TEST: Initialer PASS besitzt keine Pooldaten.'
        exit 23
    }

    $pinnedPid = [int]$baseline.ProcessId
    $pinnedStart = [DateTime]$baseline.ProcessStartedUtc
    $pinnedProfile = [string]$baseline.Pools.BinaryProfile
    $pinnedBinaryHash = [string]$baseline.Pools.BinarySha256
    $pinnedPlayerLimit = [int]$baseline.Pools.PlayerLimit
    $pinnedMobLimit = [int]$baseline.Pools.MobLimit
    $pinnedNpcLimit = [int]$baseline.Pools.NpcLimit
    $pinnedTargetHash = [string]$baseline.TargetSha256
    $pinnedBackupHash = [string]$baseline.BackupSha256

    $maxPlayer = [int]$baseline.Pools.PlayerCount
    $maxMob = [int]$baseline.Pools.MobCount
    $maxNpc = [int]$baseline.Pools.NpcCount
    $sampleCount = 1
    $watchStart = Get-Date
    $watchDeadline = $watchStart.AddSeconds($DurationSeconds)

    Write-Host ''
    Write-Host "PINNED PID:       $pinnedPid"
    Write-Host "PINNED START UTC: $pinnedStart"
    Write-Host "PINNED PROFILE:   $pinnedProfile"
    Write-Host "PINNED LIMITS:    Player $pinnedPlayerLimit / Mob $pinnedMobLimit / NPC $pinnedNpcLimit"
    Write-Host "Beobachtung startet: $($watchStart.ToString('yyyy-MM-dd HH:mm:ss'))"
    Write-Host ''

    while ((Get-Date) -lt $watchDeadline) {
        $remaining = ($watchDeadline - (Get-Date)).TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($SampleIntervalSeconds, [Math]::Max(1, [int][Math]::Ceiling($remaining))))

        $sample = Invoke-Observation -Method $method -Observer $observer -Target $targetPath -MinimumUptime $MinimumUptimeSeconds
        $sampleCount++

        if (-not $sample.Passed) {
            Write-Error "STABILITY TEST: Sample $sampleCount ist nicht PASS [$($sample.Status)]: $($sample.Detail)"
            exit 30
        }
        if ($null -eq $sample.Pools) {
            Write-Error "STABILITY TEST: Sample $sampleCount besitzt keine Pooldaten."
            exit 31
        }

        if ([int]$sample.ProcessId -ne $pinnedPid) {
            Write-Error "STABILITY TEST: PID wechselte von $pinnedPid auf $($sample.ProcessId). Neustart/Prozesswechsel erkannt."
            exit 32
        }
        if ([DateTime]$sample.ProcessStartedUtc -ne $pinnedStart) {
            Write-Error 'STABILITY TEST: Prozessstartzeit änderte sich. Neustart erkannt.'
            exit 33
        }
        if ([string]$sample.TargetSha256 -ne $pinnedTargetHash -or [string]$sample.BackupSha256 -ne $pinnedBackupHash) {
            Write-Error 'STABILITY TEST: Ziel- oder Backup-SHA änderte sich während des Beobachtungsfensters.'
            exit 34
        }
        if ([string]$sample.Pools.BinaryProfile -ne $pinnedProfile -or [string]$sample.Pools.BinarySha256 -ne $pinnedBinaryHash) {
            Write-Error 'STABILITY TEST: Binary-Profil oder Binary-SHA änderte sich während des Beobachtungsfensters.'
            exit 35
        }
        if ([int]$sample.Pools.PlayerLimit -ne $pinnedPlayerLimit -or
            [int]$sample.Pools.MobLimit -ne $pinnedMobLimit -or
            [int]$sample.Pools.NpcLimit -ne $pinnedNpcLimit) {
            Write-Error 'STABILITY TEST: Runtime-Pool-Maxima änderten sich während des Beobachtungsfensters.'
            exit 36
        }

        $maxPlayer = [Math]::Max($maxPlayer, [int]$sample.Pools.PlayerCount)
        $maxMob = [Math]::Max($maxMob, [int]$sample.Pools.MobCount)
        $maxNpc = [Math]::Max($maxNpc, [int]$sample.Pools.NpcCount)

        $elapsed = ((Get-Date) - $watchStart).TotalSeconds
        Write-Host ("[{0,6:F0}s] PASS #{1}: P {2}/{3} · M {4}/{5} · N {6}/{7}" -f `
            $elapsed, $sampleCount,
            $sample.Pools.PlayerCount, $sample.Pools.PlayerLimit,
            $sample.Pools.MobCount, $sample.Pools.MobLimit,
            $sample.Pools.NpcCount, $sample.Pools.NpcLimit)
    }

    $final = Invoke-Observation -Method $method -Observer $observer -Target $targetPath -MinimumUptime $MinimumUptimeSeconds
    $sampleCount++
    if (-not $final.Passed -or $null -eq $final.Pools) {
        Write-Error "STABILITY TEST: finale Verifikation nicht PASS: $($final.Detail)"
        exit 40
    }
    if ([int]$final.ProcessId -ne $pinnedPid -or [DateTime]$final.ProcessStartedUtc -ne $pinnedStart) {
        Write-Error 'STABILITY TEST: Prozess wechselte unmittelbar vor der finalen Verifikation.'
        exit 41
    }
    if ([string]$final.TargetSha256 -ne $pinnedTargetHash -or
        [string]$final.BackupSha256 -ne $pinnedBackupHash -or
        [string]$final.Pools.BinaryProfile -ne $pinnedProfile -or
        [string]$final.Pools.BinarySha256 -ne $pinnedBinaryHash -or
        [int]$final.Pools.PlayerLimit -ne $pinnedPlayerLimit -or
        [int]$final.Pools.MobLimit -ne $pinnedMobLimit -or
        [int]$final.Pools.NpcLimit -ne $pinnedNpcLimit) {
        Write-Error 'STABILITY TEST: finale Hash-/Profil-/Limit-Verifikation weicht vom gepinnten Zustand ab.'
        exit 42
    }

    $maxPlayer = [Math]::Max($maxPlayer, [int]$final.Pools.PlayerCount)
    $maxMob = [Math]::Max($maxMob, [int]$final.Pools.MobCount)
    $maxNpc = [Math]::Max($maxNpc, [int]$final.Pools.NpcCount)

    Write-Host ''
    Write-Host 'RUNTIME STABILITY TEST: PASS'
    Write-Host "Samples:              $sampleCount"
    Write-Host "Unveraenderte PID:    $pinnedPid"
    Write-Host "Profil:                $pinnedProfile"
    Write-Host "Max. Belegung Player: $maxPlayer / $pinnedPlayerLimit"
    Write-Host "Max. Belegung Mob:    $maxMob / $pinnedMobLimit"
    Write-Host "Max. Belegung NPC:    $maxNpc / $pinnedNpcLimit"
    Write-Host ''
    Write-Host 'Hinweis: PASS beweist Prozess-/Poolstabilitaet im Beobachtungsfenster. Startup-/Service-Logs weiterhin separat pruefen.'
    exit 0
}
finally {
    Pop-Location
}
