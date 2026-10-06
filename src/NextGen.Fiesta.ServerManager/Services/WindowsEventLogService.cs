using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class WindowsEventLogService
{
    private readonly CommandRunner _runner;
    private static readonly Regex ServiceNameRx = new(@"_(?:Zone\d+|WorldManager|Login|Character|GameLog|AccountLog|Account|GamigoZR)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExeRx = new(@"\b(?:Zone|WorldManager|Login|Character|GameLog|AccountLog|Account|GamigoZR)\.exe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public WindowsEventLogService(CommandRunner runner) => _runner = runner;

    public async Task<IReadOnlyList<DiagnosticIssue>> AnalyzeRecentAsync(TimeSpan age, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DiagnosticIssue>();
        var ms = Math.Max(60_000, (long)age.TotalMilliseconds);
        var issues = new List<DiagnosticIssue>();

        // Application Error / WER style crashes.
        var application = await _runner.RunAsync(
            "wevtutil.exe",
            $"qe Application /q:\"*[System[TimeCreated[timediff(@SystemTime) <= {ms}] and (Level=1 or Level=2)]]\" /f:text /rd:true /c:120",
            timeoutMs: 12000,
            cancellationToken: ct);
        if (application.Success)
        {
            foreach (var block in SplitEvents(application.StdOut))
            {
                var exe = ExeRx.Match(block);
                if (!exe.Success) continue;
                if (!ContainsCrashWords(block)) continue;
                issues.Add(new DiagnosticIssue
                {
                    Code = "NG-OS-0001",
                    Severity = DiagnosticSeverity.Critical,
                    Title = "Windows meldet einen Fiesta-Prozessabsturz",
                    Description = $"Im Windows-Anwendungsprotokoll wurde ein Fehler für {exe.Value} gefunden.",
                    Recommendation = "Zeitpunkt mit Message/Dbg-Log korrelieren. Falls eine Fault-/Exception-Adresse vorhanden ist, mit der passenden PDB auflösen und zuerst den unmittelbar vorhergehenden Serverfehler untersuchen.",
                    Source = "Windows Event Log / Application",
                    Evidence = Compact(block, 1200),
                    Confidence = 0.98
                });
            }
        }

        // Service Control Manager 7031/7034 = unexpected termination.
        var system = await _runner.RunAsync(
            "wevtutil.exe",
            $"qe System /q:\"*[System[TimeCreated[timediff(@SystemTime) <= {ms}] and (EventID=7031 or EventID=7034)]]\" /f:text /rd:true /c:120",
            timeoutMs: 12000,
            cancellationToken: ct);
        if (system.Success)
        {
            foreach (var block in SplitEvents(system.StdOut))
            {
                var service = ServiceNameRx.Match(block);
                if (!service.Success) continue;
                issues.Add(new DiagnosticIssue
                {
                    Code = "NG-SVC-0003",
                    Severity = DiagnosticSeverity.Error,
                    Title = "Windows-Dienst wurde unerwartet beendet",
                    Description = $"Der Service Control Manager protokollierte ein unerwartetes Ende von {service.Value}.",
                    Recommendation = "Runtime-Timeline und die letzten Logzeilen vor diesem Zeitpunkt prüfen. Bei Zone-Ausfällen auch Startup-Reihenfolge und Recovery-Einstellungen kontrollieren.",
                    Source = "Windows Event Log / System",
                    ServiceName = service.Value,
                    Evidence = Compact(block, 1000),
                    Confidence = 0.98,
                    RepairAction = RepairActionKind.ConfigureRecovery
                });
            }
        }

        return issues;
    }

    private static bool ContainsCrashWords(string block) =>
        block.Contains("Application Error", StringComparison.OrdinalIgnoreCase)
        || block.Contains("Application Crash", StringComparison.OrdinalIgnoreCase)
        || block.Contains("faulting application", StringComparison.OrdinalIgnoreCase)
        || block.Contains("fehlerhafte Anwendung", StringComparison.OrdinalIgnoreCase)
        || block.Contains("exception code", StringComparison.OrdinalIgnoreCase)
        || block.Contains("Ausnahmecode", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitEvents(string text)
    {
        return Regex.Split(text, @"(?m)^Event\[\d+\]:")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0);
    }

    private static string Compact(string text, int max)
    {
        var value = Regex.Replace(text, @"\s+", " ").Trim();
        return value.Length <= max ? value : value[..max] + " …";
    }
}
