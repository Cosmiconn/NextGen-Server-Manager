param(
    [string]$Destination = "$PSScriptRoot\..\_restored-source"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$baseline = Join-Path $repoRoot 'baseline\0.3.4'
$parts = Get-ChildItem -Path $baseline -Filter 'source.zip.part*' | Sort-Object Name

if (-not $parts -or $parts.Count -eq 0) {
    throw "Keine Baseline-Archivteile unter $baseline gefunden."
}

$zipPath = Join-Path $env:TEMP 'NextGen-FSM-0.3.4-source.zip'
$out = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
try {
    foreach ($part in $parts) {
        $bytes = [System.IO.File]::ReadAllBytes($part.FullName)
        $out.Write($bytes, 0, $bytes.Length)
    }
}
finally {
    $out.Dispose()
}

$expected = '8766c6647295571db242952d438560273b21413f29845bf2ff939ba0dc1affff'
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
