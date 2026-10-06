# Build-Fix 0.2.1

## Behobener Fehler

Der erste Windows-Build von 0.2 scheiterte in `Services/ConfigurationAuditor.cs` ab Zeile 107 mit `CS8997` und Folgefehlern (`CS1525`, `CS1024`, `CS1002`, `CS1513`).

Ursache war ein mehrzeiliger interpolierter C# Raw-String, dessen Inhalt direkt hinter der öffnenden `"""`-Marke begann. Bei einem mehrzeiligen Raw-String muss nach der öffnenden Marke ein Zeilenumbruch folgen.

0.2.1 schreibt den String jetzt korrekt als:

```csharp
var content = $"""
#DEFINE MY_SERVER
...
""";
```

Außerdem bricht `scripts/Build.ps1` jetzt direkt nach einem fehlgeschlagenen `restore`, `build` oder `publish` ab. Dadurch erscheinen Compilerfehler nicht mehr doppelt, weil nach einem fehlgeschlagenen `dotnet build` kein `dotnet publish` mehr gestartet wird.

## Erneut bauen

```powershell
cd C:\NextGenServerTools\NextGen-Fiesta-Server-Manager
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Check-Prerequisites.ps1
.\scripts\Build.ps1
```

Für diesen Build genügt das installierte .NET 8 SDK. Visual Studio und LLVM sind optional.
