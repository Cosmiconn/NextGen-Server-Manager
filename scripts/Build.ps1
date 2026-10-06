param(
    [switch]$SelfContained,
    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\NextGen.Fiesta.ServerManager\NextGen.Fiesta.ServerManager.csproj'

function Invoke-DotNetStep {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string[]]$Arguments
    )

    Write-Host "" 
    Write-Host "== $Name ==" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name fehlgeschlagen (ExitCode $LASTEXITCODE). Der nächste Build-Schritt wurde absichtlich nicht gestartet."
    }
}

Write-Host 'NextGen Fiesta Server Manager - Release Build' -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 SDK nicht gefunden. Siehe docs\BUILD_WINDOWS.md.'
}

$version = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'dotnet --version konnte nicht ausgeführt werden.' }
if ($version -notmatch '^8\.') {
    Write-Warning "Aktives SDK ist $version. Das Projekt zielt auf .NET 8; global.json versucht ein passendes 8.x SDK zu wählen."
}

Invoke-DotNetStep -Name 'dotnet restore' -Arguments @('restore', $project)
Invoke-DotNetStep -Name 'dotnet build' -Arguments @('build', $project, '-c', 'Release', '--no-restore')

$publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--no-restore')
if ($SelfContained) { $publishArgs += @('--self-contained', 'true') } else { $publishArgs += @('--self-contained', 'false') }
if ($SingleFile) { $publishArgs += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
Invoke-DotNetStep -Name 'dotnet publish' -Arguments $publishArgs

$publish = Join-Path (Split-Path $project -Parent) 'bin\Release\net8.0-windows\win-x64\publish'
Write-Host ''
Write-Host 'Build erfolgreich.' -ForegroundColor Green
Write-Host "Publish-Ordner: $publish" -ForegroundColor Green
