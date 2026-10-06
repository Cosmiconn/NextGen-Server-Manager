# Build Fix 0.2.2

Dieser Fix behebt die drei Compilerfehler aus dem Windows-Test von 0.2.1:

- `CS0104 Application` durch gleichzeitige WPF-/WinForms-Namespaces
- `CS0246 FileSystemWatcher` durch explizites `System.IO`
- `CS0104 Timer` durch explizites `System.Threading.Timer`

Zusätzlich wurde die WinForms-Abhängigkeit vollständig entfernt. Die Ordnerauswahl verwendet jetzt den in .NET 8 verfügbaren `Microsoft.Win32.OpenFolderDialog`.

## Test

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Check-Prerequisites.ps1
.\scripts\Build.ps1
```

Die OPTIONAL-Hinweise zu LLVM und Visual Studio blockieren den normalen Build nicht.
