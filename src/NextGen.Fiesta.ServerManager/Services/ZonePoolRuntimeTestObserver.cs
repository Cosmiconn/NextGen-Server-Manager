using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only observer for the first real 2000/12000/512 single-Zone runtime test.
/// It never starts, stops or modifies a process/file. PASS requires the exact deployed
/// image plus hash-bound ShineObjectManager runtime maxima from the running process.
/// </summary>
public sealed class ZonePoolRuntimeTestObserver
{
    public const string CertifiedRuntimeProfile = "NEXTGEN_CERTIFIED_2000_12000_512";

    private readonly ZoneObjectPoolProbe _poolProbe = new();

    public ZonePoolRuntimeTestObservation Observe(string targetZoneExePath, int minimumUptimeSeconds = 10)
    {
        minimumUptimeSeconds = Math.Clamp(minimumUptimeSeconds, 0, 3600);
        if (string.IsNullOrWhiteSpace(targetZoneExePath) || !File.Exists(targetZoneExePath))
            return Blocked("Ziel-Zone.exe wurde nicht gefunden.");

        string target;
        try { target = Path.GetFullPath(targetZoneExePath); }
        catch (Exception ex) { return Blocked("Zielpfad ist ungültig: " + ex.Message); }

        var backup = target + ".nextgen-prepatch.bak";
        var deploymentRecord = target + ".nextgen-deployment.json";
        if (!File.Exists(backup) || !File.Exists(deploymentRecord))
            return Blocked("Deployment-Backup oder Deployment-JSON fehlt. Runtime-Test ist nicht einem zertifizierten Deployment zuordenbar.", target);

        ZonePoolSingleZoneDeploymentMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<ZonePoolSingleZoneDeploymentMetadata>(File.ReadAllBytes(deploymentRecord));
        }
        catch (Exception ex)
        {
            return Blocked("Deployment-JSON konnte nicht gelesen werden: " + ex.Message, target);
        }

        if (metadata is null
            || !string.Equals(metadata.State, "DEPLOYED", StringComparison.Ordinal)
            || metadata.DeployedUtc == default
            || !PathEquals(metadata.TargetPath, target)
            || !PathEquals(metadata.BackupPath, backup)
            || metadata.PlayerCapacity != ZonePoolOfflineWriterSelfTest.PlayerTarget
            || metadata.MobCapacity != ZonePoolOfflineWriterSelfTest.MobTarget
            || metadata.NpcCapacity != ZonePoolOfflineWriterSelfTest.NpcTarget
            || metadata.VerifiedSiteCount != 83)
        {
            return Blocked("Deployment-JSON entspricht nicht dem zertifizierten DEPLOYED-Zustand 2000/12000/512 mit 83 Sites.", target);
        }

        var targetHash = TrySha256(target, out var targetHashError);
        if (targetHashError is not null)
            return Blocked("Ziel-Zone.exe konnte nicht gehasht werden: " + targetHashError, target);
        if (!targetHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase)
            || !targetHash.Equals(metadata.PatchedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked("Ziel-Zone.exe entspricht nicht dem gepinnten Deployment-Patch-SHA.", target, targetSha256: targetHash);
        }

        var backupHash = TrySha256(backup, out var backupHashError);
        if (backupHashError is not null)
            return Blocked("Baseline-Backup konnte nicht gehasht werden: " + backupHashError, target, targetSha256: targetHash);
        if (!backupHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase)
            || !backupHash.Equals(metadata.BaselineSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked("Deployment-Backup entspricht nicht dem gepinnten NA2016-Baseline-SHA.", target, targetSha256: targetHash, backupSha256: backupHash);
        }

        var inventory = ReadZoneProcessInventory();
        if (!inventory.Trusted)
            return Blocked("Zone-Prozessinventar konnte nicht vollständig und vertrauenswürdig gelesen werden: " + inventory.Detail,
                target, targetSha256: targetHash, backupSha256: backupHash);

        if (inventory.Processes.Count == 0)
            return Waiting("Noch kein Zone-Prozess läuft. Den Testserver manuell starten; der Observer startet nichts.",
                target, targetHash, backupHash);

        if (inventory.Processes.Count != 1)
        {
            return Blocked(
                $"Für den isolierten Runtime-Test muss exakt eine Zone laufen; gefunden: {inventory.Processes.Count}. " +
                string.Join(" · ", inventory.Processes.Select(x => $"PID {x.ProcessId} {x.ImagePath}")),
                target,
                targetSha256: targetHash,
                backupSha256: backupHash);
        }

        var running = inventory.Processes[0];
        if (!PathEquals(running.ImagePath, target))
        {
            return Blocked(
                $"Der einzige laufende Zone-Prozess stammt nicht aus der deployed Test-Zone: PID {running.ProcessId}, {running.ImagePath}",
                target,
                running.ProcessId,
                running.StartedUtc,
                targetHash,
                backupHash);
        }

        if (running.StartedUtc < metadata.DeployedUtc.UtcDateTime.AddSeconds(-2))
        {
            return Blocked(
                $"Zone-Prozess PID {running.ProcessId} startete vor dem registrierten Deployment. Prozesszustand ist nicht dem Testdeployment zuordenbar.",
                target,
                running.ProcessId,
                running.StartedUtc,
                targetHash,
                backupHash);
        }

        var uptime = DateTime.UtcNow - running.StartedUtc;
        if (uptime < TimeSpan.FromSeconds(minimumUptimeSeconds))
        {
            return Waiting(
                $"Zone PID {running.ProcessId} läuft erst {uptime.TotalSeconds:F1}s; warte auf mindestens {minimumUptimeSeconds}s Initialisierung.",
                target,
                targetHash,
                backupHash,
                running.ProcessId,
                running.StartedUtc);
        }

        var zone = new FiestaServiceEntry
        {
            DisplayName = "Zone Pool Runtime Test",
            ServiceName = "_ZonePoolRuntimeTest",
            Kind = FiestaServiceKind.Zone,
            DirectoryPath = Path.GetDirectoryName(target) ?? string.Empty,
            ExecutablePath = target,
            ProcessId = running.ProcessId,
            ProcessStartTime = running.StartedUtc.ToLocalTime(),
            State = ServiceRuntimeState.Running
        };

        var pools = _poolProbe.Read(zone);
        if (!pools.RuntimeVerified)
        {
            var stillInitializing = pools.Detail.Contains("noch nicht vollständig initialisiert", StringComparison.OrdinalIgnoreCase);
            return stillInitializing
                ? Waiting(
                    "ShineObjectManager ist noch nicht vollständig initialisiert: " + pools.Detail,
                    target,
                    targetHash,
                    backupHash,
                    running.ProcessId,
                    running.StartedUtc,
                    pools)
                : Blocked(
                    "Runtime-Poolprobe konnte den laufenden Prozess nicht verifizieren: " + pools.Detail,
                    target,
                    running.ProcessId,
                    running.StartedUtc,
                    targetHash,
                    backupHash,
                    pools);
        }

        if (!string.Equals(pools.BinaryProfile, CertifiedRuntimeProfile, StringComparison.Ordinal)
            || !pools.BinarySha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase)
            || pools.PlayerLimit != ZonePoolOfflineWriterSelfTest.PlayerTarget
            || pools.MobLimit != ZonePoolOfflineWriterSelfTest.MobTarget
            || pools.NpcLimit != ZonePoolOfflineWriterSelfTest.NpcTarget)
        {
            return Blocked(
                $"Runtime-Profil stimmt nicht: {pools.BinaryProfile}; " +
                $"Player {pools.PlayerLimit}, Mob {pools.MobLimit}, NPC {pools.NpcLimit}.",
                target,
                running.ProcessId,
                running.StartedUtc,
                targetHash,
                backupHash,
                pools);
        }

        try
        {
            using var process = Process.GetProcessById(running.ProcessId);
            process.Refresh();
            if (process.HasExited)
                return Blocked("Zone-Prozess beendete sich während der Runtime-Verifikation.", target, running.ProcessId, running.StartedUtc, targetHash, backupHash, pools);
        }
        catch (Exception ex)
        {
            return Blocked("Zone-Prozess konnte nach der Poolprobe nicht erneut bestätigt werden: " + ex.Message,
                target, running.ProcessId, running.StartedUtc, targetHash, backupHash, pools);
        }

        return new ZonePoolRuntimeTestObservation
        {
            Passed = true,
            Status = "PASS",
            TargetPath = target,
            ProcessId = running.ProcessId,
            ProcessStartedUtc = running.StartedUtc,
            TargetSha256 = targetHash,
            BackupSha256 = backupHash,
            Pools = pools,
            Detail = $"PASS: Zertifizierte Patch-Zone läuft aus dem registrierten Zielpfad; " +
                     $"ShineObjectManager Runtime-Maxima Player {pools.PlayerLimit:N0}, Mob {pools.MobLimit:N0}, NPC {pools.NpcLimit:N0}. " +
                     $"Belegung: Player {pools.PlayerCount:N0}, Mob {pools.MobCount:N0}, NPC {pools.NpcCount:N0}."
        };
    }

    private static ZoneProcessInventory ReadZoneProcessInventory()
    {
        var result = new List<ZoneProcessInfo>();
        try
        {
            foreach (var process in Process.GetProcessesByName("Zone"))
            {
                using (process)
                {
                    try
                    {
                        var image = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(image))
                            return new ZoneProcessInventory(false, result, $"Imagepfad von PID {process.Id} ist nicht lesbar.");
                        var started = process.StartTime.ToUniversalTime();
                        result.Add(new ZoneProcessInfo(process.Id, Path.GetFullPath(image), started));
                    }
                    catch (Exception ex)
                    {
                        return new ZoneProcessInventory(false, result, $"PID {process.Id}: {ex.Message}");
                    }
                }
            }
            return new ZoneProcessInventory(true, result, "OK");
        }
        catch (Exception ex)
        {
            return new ZoneProcessInventory(false, result, ex.Message);
        }
    }

    private static string TrySha256(string path, out string? error)
    {
        try
        {
            using var stream = File.OpenRead(path);
            error = null;
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return string.Empty;
        }
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ZonePoolRuntimeTestObservation Waiting(
        string detail,
        string targetPath,
        string targetHash,
        string backupHash,
        int? pid = null,
        DateTime? startedUtc = null,
        ZoneObjectPoolRuntimeSnapshot? pools = null)
        => new()
        {
            Waiting = true,
            Status = "WAIT",
            Detail = detail,
            TargetPath = targetPath,
            ProcessId = pid,
            ProcessStartedUtc = startedUtc,
            TargetSha256 = targetHash,
            BackupSha256 = backupHash,
            Pools = pools
        };

    private static ZonePoolRuntimeTestObservation Blocked(
        string detail,
        string targetPath = "",
        int? processId = null,
        DateTime? processStartedUtc = null,
        string targetSha256 = "",
        string backupSha256 = "",
        ZoneObjectPoolRuntimeSnapshot? pools = null)
        => new()
        {
            Blocked = true,
            Status = "BLOCKED",
            Detail = detail,
            TargetPath = targetPath,
            ProcessId = processId,
            ProcessStartedUtc = processStartedUtc,
            TargetSha256 = targetSha256,
            BackupSha256 = backupSha256,
            Pools = pools
        };

    private sealed record ZoneProcessInfo(int ProcessId, string ImagePath, DateTime StartedUtc);
    private sealed record ZoneProcessInventory(bool Trusted, IReadOnlyList<ZoneProcessInfo> Processes, string Detail);
}

public sealed class ZonePoolRuntimeTestObservation
{
    public bool Passed { get; init; }
    public bool Waiting { get; init; }
    public bool Blocked { get; init; }
    public string Status { get; init; } = "BLOCKED";
    public string TargetPath { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public DateTime? ProcessStartedUtc { get; init; }
    public string TargetSha256 { get; init; } = string.Empty;
    public string BackupSha256 { get; init; } = string.Empty;
    public ZoneObjectPoolRuntimeSnapshot? Pools { get; init; }
    public string Detail { get; init; } = string.Empty;
}
