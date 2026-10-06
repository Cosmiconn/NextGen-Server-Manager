$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\NextGen.Fiesta.ServerManager\NextGen.Fiesta.ServerManager.csproj'
$publishDir = Join-Path $root 'src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\win-x64\publish'
$exe = Join-Path $publishDir 'NextGen.Fiesta.ServerManager.exe'

if (-not (Test-Path $exe)) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(
            ".NET 8 SDK wurde nicht gefunden.`n`nInstalliere Visual Studio mit .NET Desktop Development oder das .NET 8 SDK und starte Launch.ps1 erneut.",
            "NextGen Fiesta Server Manager",
            'OK',
            'Information') | Out-Null
        exit 1
    }
    Write-Host 'Erster Start: Release wird gebaut...' -ForegroundColor Cyan
    dotnet restore $project
    dotnet publish $project -c Release -r win-x64 --self-contained false --no-restore
}

Start-Process $exe -Verb RunAs
