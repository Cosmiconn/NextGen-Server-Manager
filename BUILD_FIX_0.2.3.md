# Build Fix 0.2.3

Der Windows-Build von 0.2.2 erreichte `dotnet build`, brach dort aber mit 99 Fehlern ab.

## Root Cause

Fast alle Fehler waren dieselbe Ursache: die IO-Typen wurden im Projekt nicht aufgelöst. Betroffen waren u.a.:

- `File`
- `Path`
- `Directory`
- `SearchOption`
- `FileStream`
- `StreamReader`
- `IOException`
- `FileMode` / `FileAccess` / `FileShare`

Dazu kam ein echter unabhängiger Fehler in `WindowsServiceManager.cs`: ein Ternary mit `int` und `null` wurde mit `var` deklariert.

## Fix

0.2.3 enthält `GlobalUsings.cs`:

```csharp
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
```

und der PID-Wert ist jetzt explizit nullable:

```csharp
int? pid = condition ? p : null;
```

Die `ReferenceAuditService`-Count- und `MainViewModel`-`path`-Fehler waren Compiler-Kaskaden aus dem fehlenden IO-Namespace und sollten mit diesem Fix verschwinden.

## Rebuild

```powershell
cd C:\NextGenServerTools\NextGen-Fiesta-Server-Manager
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Check-Prerequisites.ps1
.\scripts\Build.ps1
```

Erwartet wird als nächster Meilenstein ein erfolgreicher `dotnet build`; danach läuft automatisch `dotnet publish`.
