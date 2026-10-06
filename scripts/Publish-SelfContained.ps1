$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build.ps1') -SelfContained -SingleFile
