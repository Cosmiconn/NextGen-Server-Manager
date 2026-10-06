$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\win-x64\publish\NextGen.Fiesta.ServerManager.exe'
$log = Join-Path $env:LOCALAPPDATA 'NextGenFiestaServerManager\startup.log'

Write-Host 'NextGen Fiesta Server Manager - Startup Diagnostics' -ForegroundColor Cyan
Write-Host "EXE: $exe"
Write-Host "Log: $log"

if (!(Test-Path $exe)) {
    Write-Host '[FEHLER] Publish-EXE nicht gefunden. Erst .\scripts\Build.ps1 ausführen.' -ForegroundColor Red
    exit 2
}

if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4

if ($p.HasExited) {
    Write-Host "[FEHLER] Prozess wurde sofort beendet. ExitCode=$($p.ExitCode)" -ForegroundColor Red
    if (Test-Path $log) {
        Write-Host "`n--- startup.log ---" -ForegroundColor Yellow
        Get-Content $log
    } else {
        Write-Host '[FEHLER] Kein startup.log erzeugt. Dann liegt der Fehler wahrscheinlich vor dem .NET/WPF-App-Start (Loader/Runtime/Windows).' -ForegroundColor Red
        Write-Host 'Letzte Application-Events:' -ForegroundColor Yellow
        Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'NextGen\.Fiesta\.ServerManager|\.NET Runtime|Application Error' } |
            Select-Object TimeCreated, Id, ProviderName, Message |
            Format-List
    }
    exit 1
}

Write-Host "[OK] Prozess läuft nach 4 Sekunden. PID=$($p.Id)" -ForegroundColor Green
if (Test-Path $log) {
    Write-Host "`n--- startup.log ---" -ForegroundColor Yellow
    Get-Content $log
}
