# Windows Build-Anleitung (Deutsch)

Der Manager ist eine **WPF/.NET-8-Windows-Anwendung**. Für den normalen Build brauchst du nur das **.NET 8 SDK**. Visual Studio ist komfortabel, aber nicht zwingend. LLVM ist nur für die tiefe PDB-/Symbolanalyse nötig.

## 0. Schnellste Variante

Wenn du `winget` installiert hast, kann das Projekt die Build-Voraussetzungen selbst anstoßen. Für den **minimalen Build** genügt:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Install-Build-Prerequisites.ps1
```

Optional inklusive Visual Studio Community und LLVM:

```powershell
.\scripts\Install-Build-Prerequisites.ps1 -WithVisualStudio -WithLlvm
```

Danach PowerShell **schließen und neu öffnen**. Eine kurze Ein-Seiten-Anleitung liegt zusätzlich unter `docs\BUILD_QUICKSTART.md`.

## 1. Pflicht: .NET 8 SDK installieren

Stand 05.10.2026 ist auf der offiziellen .NET-8-Seite **SDK 8.0.425** der aktuelle 8.0-SDK-Build. Verwende beim manuellen Download immer den jeweils aktuell angebotenen 8.0.x-Patch.

Offizieller Microsoft-Download:

https://dotnet.microsoft.com/en-us/download/dotnet/8.0

Auf der Seite unter **SDK / Windows / x64** den Installer laden. Das SDK enthält auch die benötigte .NET Desktop Runtime.

Direkter Microsoft-Landing-Link für den aktuell geprüften Windows-x64-Installer (SDK 8.0.425):

https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/sdk-8.0.425-windows-x64-installer

Falls Microsoft inzwischen einen neueren 8.0.x-Patch veröffentlicht hat, bevorzuge die allgemeine .NET-8-Seite oben.

Danach **PowerShell neu öffnen** und prüfen:

```powershell
dotnet --version
dotnet --list-sdks
```

Es muss mindestens ein Eintrag mit `8.0.x` vorhanden sein.

Optional über Windows Package Manager:

```powershell
winget install --id Microsoft.DotNet.SDK.8 -e
```

## 2. Optional, empfohlen: Visual Studio Community

Offizielle Download-Seite:

https://visualstudio.microsoft.com/downloads/

Aktuell ist **Visual Studio Community 2026** verfügbar; Visual Studio 2022 oder neuer funktioniert für dieses Projekt ebenfalls. Im Visual-Studio-Installer unbedingt die Workload auswählen:

**.NET-Desktopentwicklung / .NET Desktop Development**

Für dieses Projekt sind keine C++-Workloads nötig.

## 3. Optional: LLVM für `llvm-pdbutil`

Nur nötig, wenn im Manager die Registerkarte **PDB / Symbole** benutzt werden soll.

Offizielle LLVM-Releases:

https://github.com/llvm/llvm-project/releases

Für einen normalen Intel-/AMD-Windows-PC den aktuellen **Windows x64 Installer** verwenden. Stand 05.10.2026 ist auf der offiziellen Release-Seite LLVM 23.1.2 als aktuelle Version gelistet. Die Versionsnummer ist für das Tool nicht fest verdrahtet; entscheidend ist, dass `llvm-pdbutil.exe` vorhanden ist. Bei der Installation LLVM zum `PATH` hinzufügen oder danach den LLVM-`bin`-Ordner manuell in PATH aufnehmen.

Prüfen:

```powershell
where.exe llvm-pdbutil.exe
llvm-pdbutil.exe --version
```

Optional über winget:

```powershell
winget install --id LLVM.LLVM -e
```

## 4. Projekt entpacken

Beispiel:

```text
C:\Dev\NextGen-Fiesta-Server-Manager\
```

Nicht direkt im Fiesta-Serverordner entwickeln. Der fertige Manager kann später jeden NA2016-Serverordner auswählen.

## 5. Voraussetzungen automatisch prüfen

PowerShell im Projektordner öffnen:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Check-Prerequisites.ps1
```

`Set-ExecutionPolicy -Scope Process` gilt nur für dieses PowerShell-Fenster.

## 6A. Empfohlener Build per PowerShell / dotnet CLI

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Build.ps1
```

Das Script führt aus:

1. `dotnet restore`
2. `dotnet build -c Release`
3. `dotnet publish -c Release -r win-x64`

Die Ausgabe liegt danach hier:

```text
src\NextGen.Fiesta.ServerManager\bin\Release\net8.0-windows\win-x64\publish\
```

Startdatei:

```text
NextGen.Fiesta.ServerManager.exe
```

### Framework-dependent Build

Standardmäßig muss auf dem Ziel-PC die .NET 8 Desktop Runtime vorhanden sein. Der Build ist dafür kleiner.

### Self-contained Single-File Build

Wenn auf dem Server-PC **kein .NET installiert** sein soll:

```powershell
.\scripts\Publish-SelfContained.ps1
```

oder:

```powershell
.\scripts\Build.ps1 -SelfContained -SingleFile
```

Der Output ist größer, bringt aber die Runtime mit.

## 6B. Build mit Visual Studio

1. `NextGen.Fiesta.ServerManager.sln` öffnen.
2. Oben Konfiguration **Release** auswählen.
3. Plattform **x64** auswählen. Falls x64 nicht angezeigt wird, `Any CPU` ist für den normalen Build ebenfalls möglich; Publish wird trotzdem gezielt als `win-x64` erzeugt.
4. Menü **Erstellen → Projektmappe erstellen**.
5. Für eine Publish-Ausgabe entweder `scripts\Build.ps1` verwenden oder im Projekt über **Publish/Veröffentlichen** ein Folder-Profil `win-x64` erstellen.

## 7. Schnellstart ohne manuelle Build-Befehle

```powershell
.\Launch.ps1
```

Wenn noch kein Release-Publish existiert, baut `Launch.ps1` das Projekt zuerst und startet den Manager danach per UAC als Administrator.

## 8. Erster Test

1. Manager als Administrator starten.
2. Als **Server Root** den Ordner wählen, der `9Data`, `WorldManager`, `Zone00` usw. enthält.
3. `Scannen`.
4. Prüfen, ob Account/WorldManager/Zonen erkannt werden.
5. `Vollanalyse` ausführen.
6. Unter **Live Timeline** den Monitor starten bzw. kontrollieren, ob er automatisch aktiv ist.
7. Zum Hochfahren bevorzugt **Smart Start** benutzen.

## 9. Häufige Buildfehler

### CS8997 / Fehler ab `ConfigurationAuditor.cs(107,...)`

Dieser Fehler betraf den ersten 0.2-Stand des Projekts. In **0.2.2** sind sowohl der mehrzeilige Raw-String als auch die Windows-Namespace-Konflikte korrigiert. Wenn du noch 0.2 oder 0.2.1 verwendest, ersetze den Projektordner durch 0.2.2 und baue erneut. Die installierte .NET-8-Version ist dabei nicht die Ursache.


### `dotnet` wurde nicht gefunden

.NET 8 SDK installieren und danach PowerShell komplett schließen und neu öffnen.

### `A compatible installed .NET SDK ... was not found`

```powershell
dotnet --list-sdks
```

Es fehlt ein .NET-8-SDK. Eine reine Runtime genügt zum Bauen nicht.

### WPF-/WindowsDesktop-Fehler

Sicherstellen, dass unter Windows gebaut wird und das **.NET 8 SDK** vollständig installiert ist. Bei Visual Studio die Workload **.NET Desktop Development** nachinstallieren.

### `llvm-pdbutil` fehlt

Das verhindert **nicht** den normalen Build oder die Serververwaltung. Nur die PDB-Tiefensuche ist dann nicht verfügbar.

### PowerShell blockiert `.ps1`

Nur für das aktuelle Fenster freigeben:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
```

## 10. Direkter manueller Build

```powershell
dotnet restore .\NextGen.Fiesta.ServerManager.sln
dotnet build .\NextGen.Fiesta.ServerManager.sln -c Release
dotnet publish .\src\NextGen.Fiesta.ServerManager\NextGen.Fiesta.ServerManager.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false
```

Self-contained:

```powershell
dotnet publish .\src\NextGen.Fiesta.ServerManager\NextGen.Fiesta.ServerManager.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

## 11. Was zum Bauen NICHT benötigt wird

- SQL Server ist für den **Build** nicht nötig.
- Fiesta muss für den **Build** nicht laufen.
- PDBs sind für den **Build** nicht nötig.
- LLVM ist für den **Build** nicht nötig.

SQL/Fiesta/PDBs werden erst bei der tatsächlichen Diagnose eines NA2016-Servers relevant.
