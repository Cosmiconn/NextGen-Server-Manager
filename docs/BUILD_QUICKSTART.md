# Build-Quickstart für Windows

Für den **Build** des NextGen Fiesta Server Managers wird nur das **.NET 8 SDK (x64)** benötigt. Visual Studio ist optional. LLVM ist nur für die PDB-Tiefenanalyse nötig.

## Download

### Pflicht: .NET 8 SDK x64

- Offizielle .NET-8-Seite: <https://dotnet.microsoft.com/en-us/download/dotnet/8.0>
- Dort **SDK → Windows → x64 Installer** wählen.
- Alternativ in PowerShell:

```powershell
winget install --id Microsoft.DotNet.SDK.8 -e
```

### Optional: Visual Studio Community 2026

- Offizielle Download-Seite: <https://visualstudio.microsoft.com/downloads/>
- **Visual Studio Community 2026** installieren.
- Im Installer die Workload **.NET-Desktopentwicklung / .NET Desktop Development** markieren.

Alternativ per winget:

```powershell
winget install --id Microsoft.VisualStudio.Community -e `
  --override "--passive --add Microsoft.VisualStudio.Workload.ManagedDesktop --includeRecommended"
```

### Optional: LLVM / llvm-pdbutil

- Offizielle Releases: <https://github.com/llvm/llvm-project/releases>
- Für normale Intel-/AMD-PCs den **Windows x64 Installer** der aktuellen LLVM-Version wählen.

Alternativ:

```powershell
winget install --id LLVM.LLVM -e
```

## Einfachster Build

ZIP in einen normalen Entwicklungsordner entpacken, z. B.:

```text
C:\Dev\NextGen-Fiesta-Server-Manager\
```

PowerShell in diesem Ordner öffnen:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Check-Prerequisites.ps1
.\scripts\Build.ps1
```

Fertige Dateien:

```text
src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\win-x64\publish\
```

Startdatei:

```text
NextGen.Fiesta.ServerManager.exe
```

## Portable/Self-contained Build

Wenn der Ziel-PC keine .NET Runtime installiert haben soll:

```powershell
.\scripts\Publish-SelfContained.ps1
```

## Automatische Installation der Build-Voraussetzungen

Nur .NET 8 SDK:

```powershell
.\scripts\Install-Build-Prerequisites.ps1
```

Zusätzlich Visual Studio und LLVM:

```powershell
.\scripts\Install-Build-Prerequisites.ps1 -WithVisualStudio -WithLlvm
```

Danach PowerShell **neu öffnen**, dann `Check-Prerequisites.ps1` und `Build.ps1` ausführen.

Die ausführliche Anleitung steht in `docs\BUILD_WINDOWS.md`.
