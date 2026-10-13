using System.Diagnostics;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only, deliberately NON-CERTIFYING original-NA2016 Zone00/Zone01 debugger observer.
/// Unlike ZonePoolRuntimeTestObserver, this accepts the hash-pinned stock Zone.exe
/// and allows other zones to run. It never modifies server files or process memory.
/// </summary>
internal sealed class ZoneLocalDiagnosticObserver
{
    private readonly ZoneObjectPoolProbe _poolProbe = new();

    public ZonePoolRuntimeTestObservation Observe(
        string targetZoneExePath,
        string requiredFolder = "Zone01",
        int? expectedProcessId = null,
        DateTime? expectedStartUtc = null)
    {
        if (requiredFolder is not ("Zone00" or "Zone01"))
            return Blocked("Unbekannte Diagnosezone.");
        if (string.IsNullOrWhiteSpace(targetZoneExePath) || !File.Exists(targetZoneExePath))
            return Blocked("Testzonen-EXE wurde nicht gefunden.");

        string target;
        try { target = Path.GetFullPath(targetZoneExePath); }
        catch (Exception ex) { return Blocked("Ungültiger Testzonen-Pfad: " + ex.Message); }

        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(target)),
                requiredFolder, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(target), "Zone.exe", StringComparison.OrdinalIgnoreCase))
            return Blocked($"Dieser Diagnosemodus erlaubt nur {requiredFolder}\\Zone.exe.");

        try
        {
            var matches = new List<(int Pid, DateTime StartUtc)>();
            foreach (var proc in Process.GetProcessesByName("Zone"))
            {
                using (proc)
                {
                    // Never select a process by its name alone.
                    var runningPath = proc.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(runningPath))
                        return Blocked("Zone-Prozesspfad kann nicht zuverlässig gelesen werden; bitte als Administrator starten.");
                    if (!string.Equals(Path.GetFullPath(runningPath), target,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    matches.Add((proc.Id, proc.StartTime.ToUniversalTime()));
                }
            }

            if (matches.Count != 1)
                return Blocked($"Testzonen-Prozess nicht eindeutig: {matches.Count} exakte Instanz(en) von {target}.", target);
            var (pid, started) = matches[0];
            if (expectedProcessId.HasValue && (pid != expectedProcessId.Value
                || (expectedStartUtc.HasValue && started != expectedStartUtc.Value)))
                return Blocked("Zone01-PID oder Startzeit hat sich während des Diagnoselaufs geändert.", target);

            var zone = new FiestaServiceEntry
            {
                DisplayName = requiredFolder + " Diagnostic",
                ServiceName = "_ZoneLocalDiagnostic",
                Kind = FiestaServiceKind.Zone,
                DirectoryPath = Path.GetDirectoryName(target) ?? string.Empty,
                ExecutablePath = target,
                ProcessId = pid,
                ProcessStartTime = started.ToLocalTime(),
                State = ServiceRuntimeState.Running
            };
            var pool = _poolProbe.Read(zone);
            if (!pool.RuntimeVerified)
                return Blocked("Testzonen-ShinePlayer-Runtimeprobe fehlgeschlagen: " + pool.Detail, target);
            return new ZonePoolRuntimeTestObservation
            {
                Passed = true,
                Status = "DIAGNOSTIC_READY",
                TargetPath = target,
                ProcessId = pid,
                ProcessStartedUtc = started,
                TargetSha256 = pool.BinarySha256,
                Pools = pool,
                Detail = $"NICHT ZERTIFIZIEREND · {requiredFolder} PID {pid}, Original-/Patch-Hash geprüft; " +
                         $"ShinePlayer {pool.PlayerCount:N0}/{pool.PlayerLimit:N0}. " +
                         "Andere Zonen dürfen laufen, gezählt wird ausschließlich {requiredFolder}."
            };
        }
        catch (Exception ex)
        {
            return Blocked("Testzonen-Prozessinventar nicht verifizierbar: " + ex.Message, target);
        }
    }

    private static ZonePoolRuntimeTestObservation Blocked(string detail, string target = "")
        => new() { Blocked = true, Status = "BLOCKED", TargetPath = target, Detail = detail };

}
