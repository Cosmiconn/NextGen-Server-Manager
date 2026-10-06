$ErrorActionPreference = 'Continue'

Write-Host 'NextGen Fiesta Server Manager - Voraussetzungen' -ForegroundColor Cyan
Write-Host ''

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    Write-Host '[OK] dotnet gefunden:' $dotnet.Source -ForegroundColor Green
    dotnet --version
    $sdks = dotnet --list-sdks
    if ($sdks -match '^8\.') { Write-Host '[OK] .NET 8 SDK installiert' -ForegroundColor Green }
    else { Write-Host '[FEHLT] .NET 8 SDK ist nicht installiert.' -ForegroundColor Red }
} else {
    Write-Host '[FEHLT] dotnet.exe / .NET SDK' -ForegroundColor Red
}

function Find-LlvmPdbUtil {
    $cmd = Get-Command llvm-pdbutil.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        $env:NEXTGEN_LLVM_PDBUTIL,
        $env:LLVM_PDBUTIL,
        (Join-Path $env:ProgramFiles 'LLVM\bin\llvm-pdbutil.exe'),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'LLVM\bin\llvm-pdbutil.exe' }),
        (Join-Path $env:LOCALAPPDATA 'Programs\LLVM\bin\llvm-pdbutil.exe'),
        $(if ($env:ChocolateyInstall) { Join-Path $env:ChocolateyInstall 'bin\llvm-pdbutil.exe' })
    ) | Where-Object { $_ -and (Test-Path $_) }

    return $candidates | Select-Object -First 1
}

$llvm = Find-LlvmPdbUtil
if ($llvm) {
    Write-Host '[OK] llvm-pdbutil gefunden:' $llvm -ForegroundColor Green
    & $llvm --version | Select-Object -First 1
} else {
    Write-Host '[OPTIONAL] llvm-pdbutil nicht gefunden. PDB-Tiefenanalyse bleibt deaktiviert.' -ForegroundColor Yellow
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Workload.ManagedDesktop -property installationPath
    if ($vs) { Write-Host '[OK] Visual Studio mit .NET Desktop Development:' $vs -ForegroundColor Green }
    else { Write-Host '[OPTIONAL] Visual Studio gefunden, aber .NET Desktop Development konnte nicht bestätigt werden.' -ForegroundColor Yellow }
} else {
    Write-Host '[OPTIONAL] Visual Studio nicht erkannt. Für den CLI-Build genügt das .NET 8 SDK.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Download-Hinweise: docs\BUILD_WINDOWS.md' -ForegroundColor Cyan
