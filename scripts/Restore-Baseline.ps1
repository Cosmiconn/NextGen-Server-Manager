param(
    [string]$Destination = "$PSScriptRoot\..\_restored-source"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$baseline = Join-Path $repoRoot 'baseline\0.3.4'
$zipPath = Join-Path $env:TEMP 'NextGen-FSM-0.3.4-source.zip'

$rawParts = @(Get-ChildItem -Path $baseline -Filter 'source.zip.part*' -ErrorAction SilentlyContinue | Sort-Object Name)
$b64Parts = @(Get-ChildItem -Path $baseline -Filter 'source.b64.part*' -ErrorAction SilentlyContinue | Sort-Object Name)

if ($rawParts.Count -gt 0) {
    $out = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        foreach ($part in $rawParts) {
            $bytes = [System.IO.File]::ReadAllBytes($part.FullName)
            $out.Write($bytes, 0, $bytes.Length)
        }
    }
    finally {
        $out.Dispose()
    }
}
elseif ($b64Parts.Count -gt 0) {
    $builder = New-Object System.Text.StringBuilder
    foreach ($part in $b64Parts) {
        [void]$builder.Append((Get-Content -Raw -LiteralPath $part.FullName))
    }

    $base64 = ($builder.ToString() -replace '\s', '')
    $bytes = [Convert]::FromBase64String($base64)
    [System.IO.File]::WriteAllBytes($zipPath, $bytes)
}
else {
    throw "Keine Baseline-Archivteile unter $baseline gefunden (source.zip.part* oder source.b64.part*)."
}

# Hash of the decoded source ZIP currently stored in baseline/0.3.4/source.b64.part*.
$expected = '7a2b93ef0d4ed26bbc6ab83b243b40577b5bec82beb6ad9738dbdfd14d12a1e4'
$actual = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    throw "Baseline-Archiv SHA256 stimmt nicht. Erwartet $expected, erhalten $actual"
}

if (Test-Path $Destination) {
    Remove-Item -Recurse -Force $Destination
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $Destination -Force
Write-Host "Baseline 0.3.4 wiederhergestellt nach: $Destination"
