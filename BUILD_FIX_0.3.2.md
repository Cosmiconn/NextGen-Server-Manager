# 0.3.2 – Vertical Scaling / Performance Audit

Diese Version baut auf 0.3.1 auf. Neu sind `PerformanceTuningAuditService`, die Modelle `PerformanceTuningEntry`/`ProcessScalingSnapshot`, der neue WPF-Tab sowie die Exportsektion `performanceTuning`.

## Erwarteter Windows-Build

```powershell
.\scripts\Build.ps1
```

In der Linux-Arbeitsumgebung des Generators steht kein Windows-.NET/WPF-SDK zur Verfügung; deshalb wurde kein echter WPF-Compilerlauf behauptet. Statisch geprüft wurden:

- XML/XAML-Wohlgeformtheit von `App.xaml`, `MainWindow.xaml` und `.csproj`
- grobe C#-Klammerstruktur aller Quelldateien
- Versionskonsistenz 0.3.2

Bitte Buildfehler vollständig zurückgeben; sie werden in der nächsten Revision direkt behoben.
