using System.Diagnostics;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only readiness check for a future single-Zone test deployment of the certified
/// 2000/12000/512 executable copy. It never creates, deletes, renames or overwrites files.
/// </summary>
public sealed class ZonePoolDeploymentPreflight
{
    private readonly ZonePoolPatchedCopyVerifier _patchedCopyVerifier = new();

    public ZonePoolDeploymentPreflightResult Analyze(string targetZoneExePath, string patchedZoneExePath)
    {
        if (string.IsNullOrWhiteSpace(targetZoneExePath) || !File.Exists(targetZoneExePath))
            return Failed("Ziel-Zone.exe wurde nicht gefunden.");
        if (string.IsNullOrWhiteSpace(patchedZoneExePath) || !File.Exists(patchedZoneExePath))
            return Failed("Patchkopie wurde nicht gefunden.");

        var target = Path.GetFullPath(targetZoneExePath);
        var patched = Path.GetFullPath(patchedZoneExePath);
        if (string.Equals(target, patched, StringComparison.OrdinalIgnoreCase))
            return Failed("Patchkopie und Ziel-Zone.exe dürfen nicht dieselbe Datei sein.");

        var patchedVerification = _patchedCopyVerifier.Verify(patched);
        if (!patchedVerification.Success)
            return Failed("Patchkopie ist nicht vollständig verifiziert: " + patchedVerification.Detail,
                patchedVerification: patchedVerification);

        string targetHash;
        try { targetHash = Sha256File(target); }
        catch (Exception ex) { return Failed("Ziel-Zone.exe konnte nicht gehasht werden: " + ex.Message, patchedVerification: patchedVerification); }

        if (!targetHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Ziel-Zone.exe entspricht nicht der verifizierten NA2016-Baseline. Kein Austausch zulässig.",
                targetHash, patchedVerification);

        var running = FindRunningZoneProcesses();
        if (!running.ProcessEnumerationTrusted)
            return Failed("Zone-Prozessstatus konnte nicht zuverlässig geprüft werden; Preflight schlägt fail-closed fehl.",
                targetHash, patchedVerification);
        if (running.ProcessIds.Count > 0)
            return Failed("BLOCKIERT: Mindestens ein Zone-Prozess läuft: " + string.Join(", ", running.ProcessIds.Select(x => "PID " + x)),
                targetHash, patchedVerification, running.ProcessIds);

        var targetDirectory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            return Failed("Zielverzeichnis ist ungültig.", targetHash, patchedVerification);

        var deploymentBackup = target + ".nextgen-prepatch.bak";
        var deploymentRecord = target + ".nextgen-deployment.json";
        if (File.Exists(deploymentBackup) || File.Exists(deploymentRecord))
            return Failed("Geplanter Deployment-Backup- oder Metadatenpfad existiert bereits. Automatisches Überschreiben bleibt gesperrt.",
                targetHash, patchedVerification);

        long freeBytes = -1;
        try
        {
            var root = Path.GetPathRoot(targetDirectory);
            if (!string.IsNullOrWhiteSpace(root))
                freeBytes = new DriveInfo(root).AvailableFreeSpace;
        }
        catch { }

        var requiredBytes = new FileInfo(target).Length * 3L;
        if (freeBytes >= 0 && freeBytes < requiredBytes)
            return Failed($"Zu wenig freier Speicher für gestagten Austausch und Rollback: {freeBytes:N0} Byte verfügbar, mindestens {requiredBytes:N0} Byte gefordert.",
                targetHash, patchedVerification);

        return new ZonePoolDeploymentPreflightResult
        {
            Ready = true,
            TargetZoneExePath = target,
            PatchedZoneExePath = patched,
            TargetBaselineSha256 = targetHash,
            PatchedSha256 = patchedVerification.PatchedSha256,
            PlannedTargetBackupPath = deploymentBackup,
            PlannedDeploymentRecordPath = deploymentRecord,
            AvailableFreeBytes = freeBytes,
            RequiredFreeBytes = requiredBytes,
            PatchedCopyVerification = patchedVerification,
            Detail = "READY (read-only): Ziel ist die verifizierte Baseline, Patchkopie ist vollständig zertifiziert, keine Zone-Prozesse laufen, " +
                     "Backup-/Metadatenpfade sind frei und der Austauschplan kollidiert mit keiner vorhandenen Datei. Es wurde nichts verändert."
        };
    }

    private static ProcessInventory FindRunningZoneProcesses()
    {
        var ids = new List<int>();
        try
        {
            foreach (var process in Process.GetProcessesByName("Zone"))
            {
                try { ids.Add(process.Id); }
                finally { process.Dispose(); }
            }
            return new ProcessInventory(true, ids);
        }
        catch
        {
            return new ProcessInventory(false, ids);
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ZonePoolDeploymentPreflightResult Failed(
        string detail,
        string targetHash = "",
        ZonePoolPatchedCopyVerificationResult? patchedVerification = null,
        IReadOnlyList<int>? runningProcessIds = null)
        => new()
        {
            Ready = false,
            TargetBaselineSha256 = targetHash,
            PatchedCopyVerification = patchedVerification,
            RunningZoneProcessIds = runningProcessIds ?? Array.Empty<int>(),
            Detail = detail
        };

    private sealed record ProcessInventory(bool ProcessEnumerationTrusted, IReadOnlyList<int> ProcessIds);
}

public sealed class ZonePoolDeploymentPreflightResult
{
    public bool Ready { get; init; }
    public string TargetZoneExePath { get; init; } = string.Empty;
    public string PatchedZoneExePath { get; init; } = string.Empty;
    public string TargetBaselineSha256 { get; init; } = string.Empty;
    public string PatchedSha256 { get; init; } = string.Empty;
    public string PlannedTargetBackupPath { get; init; } = string.Empty;
    public string PlannedDeploymentRecordPath { get; init; } = string.Empty;
    public long AvailableFreeBytes { get; init; } = -1;
    public long RequiredFreeBytes { get; init; }
    public IReadOnlyList<int> RunningZoneProcessIds { get; init; } = Array.Empty<int>();
    public ZonePoolPatchedCopyVerificationResult? PatchedCopyVerification { get; init; }
    public string Detail { get; init; } = string.Empty;
}
