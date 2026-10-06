param(
    [switch]$WithVisualStudio,
    [switch]$WithLlvm,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

Write-Host 'NextGen Fiesta Server Manager - Build-Voraussetzungen' -ForegroundColor Cyan
Write-Host 'Pflicht: .NET 8 SDK. Optional: Visual Studio Community + LLVM.'
Write-Host ''

$winget = Get-Command winget.exe -ErrorAction SilentlyContinue
if (-not $winget) {
    Write-Host '[FEHLT] winget wurde nicht gefunden.' -ForegroundColor Red
    Write-Host 'Manuelle Downloads:' -ForegroundColor Yellow
    Write-Host '  .NET 8 SDK: https://dotnet.microsoft.com/en-us/download/dotnet/8.0'
    Write-Host '  Visual Studio Community: https://visualstudio.microsoft.com/downloads/'
    Write-Host '  LLVM: https://github.com/llvm/llvm-project/releases'
    exit 2
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory=$true)][string]$Id,
        [string[]]$ExtraArgs = @()
    )

    $args = @('install', '--id', $Id, '-e', '--accept-package-agreements', '--accept-source-agreements') + $ExtraArgs
    Write-Host ("winget " + ($args -join ' ')) -ForegroundColor DarkGray
    if ($WhatIf) { return }
    & winget.exe @args
    if ($LASTEXITCODE -ne 0) { throw "winget-Installation von '$Id' fehlgeschlagen (ExitCode $LASTEXITCODE)." }
}

# Required for building the net8.0-windows WPF application.
if (Get-Command dotnet.exe -ErrorAction SilentlyContinue) {
    $sdks = & dotnet.exe --list-sdks
    if ($sdks -match '(?m)^8\.') {
        Write-Host '[OK] .NET 8 SDK ist bereits installiert.' -ForegroundColor Green
    } else {
        Write-Host '[INSTALL] .NET 8 SDK' -ForegroundColor Yellow
        Install-WingetPackage 'Microsoft.DotNet.SDK.8'
    }
} else {
    Write-Host '[INSTALL] .NET 8 SDK' -ForegroundColor Yellow
    Install-WingetPackage 'Microsoft.DotNet.SDK.8'
}

if ($WithVisualStudio) {
    Write-Host '[INSTALL] Visual Studio Community mit .NET-Desktopentwicklung' -ForegroundColor Yellow
    # Microsoft documents the non-versioned winget ID for the current Visual Studio release.
    Install-WingetPackage 'Microsoft.VisualStudio.Community' @(
        '--override',
        '"--passive --add Microsoft.VisualStudio.Workload.ManagedDesktop --includeRecommended"'
    )
}

if ($WithLlvm) {
    Write-Host '[INSTALL] LLVM (llvm-pdbutil)' -ForegroundColor Yellow
    Install-WingetPackage 'LLVM.LLVM'
}

Write-Host ''
Write-Host 'Installation angestoßen/abgeschlossen.' -ForegroundColor Green
Write-Host 'WICHTIG: PowerShell danach schließen und neu öffnen.' -ForegroundColor Yellow
Write-Host 'Dann prüfen:'
Write-Host '  .\scripts\Check-Prerequisites.ps1'
