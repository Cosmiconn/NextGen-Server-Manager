param(
    [Parameter(Mandatory=$false)]
    [string]$ServerRoot = ""
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ServerRoot)) {
    $ServerRoot = Read-Host 'Server Root (z.B. C:\NextGenFiesta\Server)'
}

$wm = Join-Path $ServerRoot 'WorldManager\WorldManager.exe'
$zone = Join-Path $ServerRoot 'Zone00\Zone.exe'
$serverInfo = Join-Path $ServerRoot '9Data\ServerInfo\ServerInfo.txt'

$expectedWm = '23e94c78840a80f874adffa15792ae29f5d25df950418b3633f6e5f5ba68ede1'
$expectedZone = 'db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5'

function Check-Hash($path, $expected, $label) {
    if (!(Test-Path $path)) { Write-Host "[FEHLT] $label : $path" -ForegroundColor Red; return }
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -eq $expected) { Write-Host "[OK] $label Baseline-Hash" -ForegroundColor Green }
    else { Write-Host "[WARN] $label Hash abweichend: $actual" -ForegroundColor Yellow }
}

Check-Hash $wm $expectedWm 'WorldManager.exe'
Check-Hash $zone $expectedZone 'Zone00\Zone.exe'

if (Test-Path $serverInfo) {
    Write-Host "`nServerInfo relevante Limits:" -ForegroundColor Cyan
    Select-String -Path $serverInfo -Pattern 'PG_W00_WM|PG_W00_Z\d+' | ForEach-Object { $_.Line }
} else {
    Write-Host "[FEHLT] ServerInfo: $serverInfo" -ForegroundColor Red
}

$profile = Join-Path $ServerRoot '.nextgen-hooks\adaptive-profile.json'
if (Test-Path $profile) {
    Write-Host "`nAktives NextGen Hook-Profil:" -ForegroundColor Cyan
    Get-Content $profile
} else {
    Write-Host "`nNoch kein .nextgen-hooks\adaptive-profile.json vorhanden." -ForegroundColor DarkGray
}
