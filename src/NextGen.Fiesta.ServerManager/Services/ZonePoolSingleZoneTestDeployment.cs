using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Transactional deployment/rollback wrapper for ONE isolated Zone.exe test target.
/// It never starts a server process and only accepts the independently certified
/// 2000/12000/512 patch copy. Every mutation is preceded and followed by hash checks.
/// </summary>
public sealed class ZonePoolSingleZoneTestDeployment
{
    public const string DeploymentConfirmationToken = "DEPLOY-CERTIFIED-ZONE-POOL-2000-12000-512";
    public const string RollbackConfirmationToken = "ROLLBACK-CERTIFIED-ZONE-POOL-TEST";

    private readonly ZonePoolDeploymentPreflight _preflight = new();
    private readonly ZonePoolPatchedCopyVerifier _patchedCopyVerifier = new();

    public ZonePoolSingleZoneTestDeploymentResult Deploy(
        string targetZoneExePath,
        string patchedZoneExePath,
        string confirmationToken)
    {
        if (!string.Equals(confirmationToken, DeploymentConfirmationToken, StringComparison.Ordinal))
            return Failed("Expliziter Deployment-Bestätigungstoken fehlt oder ist falsch. Es wurde nichts verändert.");

        var preflight = _preflight.Analyze(targetZoneExePath, patchedZoneExePath);
        if (!preflight.Ready)
            return Failed("Deployment-Preflight ist nicht READY: " + preflight.Detail, preflight);

        var target = preflight.TargetZoneExePath;
        var patched = preflight.PatchedZoneExePath;
        var backup = preflight.PlannedTargetBackupPath;
        var record = preflight.PlannedDeploymentRecordPath;

        var verification = _patchedCopyVerifier.Verify(patched);
        if (!verification.Success
            || !verification.PatchedSha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase)
            || verification.VerifiedSiteCount != 83)
        {
            return Failed("Patchkopie bestand die unmittelbare Vor-Schreibprüfung nicht: " + verification.Detail, preflight);
        }

        var targetHash = TrySha256File(target, out var targetHashError);
        if (targetHashError is not null)
            return Failed("Ziel-Zone.exe konnte unmittelbar vor dem Staging nicht gehasht werden: " + targetHashError, preflight);
        if (!targetHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Ziel-Zone.exe änderte sich nach dem Preflight. Austausch abgebrochen.", preflight);

        var processCheck = FindRunningZoneProcesses();
        if (!processCheck.ProcessEnumerationTrusted)
            return Failed("Zone-Prozessstatus ist nicht zuverlässig prüfbar. Austausch fail-closed abgebrochen.", preflight);
        if (processCheck.ProcessIds.Count > 0)
            return Failed("BLOCKIERT: Mindestens ein Zone-Prozess läuft: " + string.Join(", ", processCheck.ProcessIds.Select(x => "PID " + x)), preflight);

        if (File.Exists(backup) || File.Exists(record))
            return Failed("Deployment-Backup oder Deployment-Metadaten wurden seit dem Preflight angelegt. Austausch abgebrochen.", preflight);

        var stage = target + ".nextgen-stage-" + Guid.NewGuid().ToString("N");
        var recordTemp = record + ".tmp-" + Guid.NewGuid().ToString("N");
        var replaced = false;

        try
        {
            var patchedBytes = File.ReadAllBytes(patched);
            WriteDurable(stage, patchedBytes);
            if (!Sha256File(stage).Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Gestagte Patchdatei bestand die SHA-256-Prüfung nicht.");

            // Close the TOCTOU window as far as practical: target hash and process state
            // are checked again immediately before the atomic filesystem replacement.
            if (!Sha256File(target).Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Ziel-Zone.exe änderte sich während des Stagings.");

            processCheck = FindRunningZoneProcesses();
            if (!processCheck.ProcessEnumerationTrusted)
                throw new IOException("Zone-Prozessstatus ist unmittelbar vor dem Austausch nicht zuverlässig prüfbar.");
            if (processCheck.ProcessIds.Count > 0)
                throw new IOException("Zone-Prozess wurde während des Stagings gestartet: " + string.Join(", ", processCheck.ProcessIds.Select(x => "PID " + x)));

            File.Replace(stage, target, backup, ignoreMetadataErrors: false);
            replaced = true;

            var deployedHash = Sha256File(target);
            var backupHash = Sha256File(backup);
            if (!deployedHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Ziel-Zone.exe besitzt nach dem atomaren Austausch nicht den gepinnten Patch-SHA.");
            if (!backupHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Deployment-Backup besitzt nach dem atomaren Austausch nicht den Baseline-SHA.");

            var metadata = new ZonePoolSingleZoneDeploymentMetadata
            {
                FormatVersion = 1,
                State = "DEPLOYED",
                DeployedUtc = DateTimeOffset.UtcNow,
                TargetPath = target,
                PatchSourcePath = patched,
                BackupPath = backup,
                BaselineSha256 = backupHash,
                PatchedSha256 = deployedHash,
                VerifiedSiteCount = verification.VerifiedSiteCount,
                PlayerCapacity = ZonePoolOfflineWriterSelfTest.PlayerTarget,
                MobCapacity = ZonePoolOfflineWriterSelfTest.MobTarget,
                NpcCapacity = ZonePoolOfflineWriterSelfTest.NpcTarget
            };
            WriteJsonDurable(recordTemp, metadata);
            File.Move(recordTemp, record);

            return new ZonePoolSingleZoneTestDeploymentResult
            {
                Success = true,
                Deployed = true,
                Preflight = preflight,
                TargetPath = target,
                BackupPath = backup,
                DeploymentRecordPath = record,
                TargetSha256 = deployedHash,
                BackupSha256 = backupHash,
                Detail = "DEPLOYED: Genau eine Zone.exe wurde atomar durch die zertifizierte 2000/12000/512-Testkopie ersetzt. " +
                         "Baseline-Backup und Deployment-Metadaten sind hashverifiziert. Es wurde KEIN Zone-Prozess gestartet."
            };
        }
        catch (Exception ex)
        {
            SafeDelete(stage);
            SafeDelete(recordTemp);

            if (replaced)
            {
                var emergency = TryEmergencyRollback(target, backup);
                return Failed(
                    "Deployment nach Dateiaustausch fehlgeschlagen: " + ex.Message + " " + emergency.Detail,
                    preflight,
                    emergencyRollbackAttempted: true,
                    emergencyRollbackSucceeded: emergency.Success,
                    targetPath: target,
                    backupPath: backup,
                    recordPath: record);
            }

            return Failed("Deployment vor dem Dateiaustausch abgebrochen: " + ex.Message, preflight);
        }
    }

    public ZonePoolSingleZoneTestDeploymentResult Rollback(
        string targetZoneExePath,
        string confirmationToken)
    {
        if (!string.Equals(confirmationToken, RollbackConfirmationToken, StringComparison.Ordinal))
            return Failed("Expliziter Rollback-Bestätigungstoken fehlt oder ist falsch. Es wurde nichts verändert.");
        if (string.IsNullOrWhiteSpace(targetZoneExePath) || !File.Exists(targetZoneExePath))
            return Failed("Ziel-Zone.exe wurde nicht gefunden.");

        var target = Path.GetFullPath(targetZoneExePath);
        var backup = target + ".nextgen-prepatch.bak";
        var record = target + ".nextgen-deployment.json";
        var patchedAuditBackup = target + ".nextgen-rollback-patched.bak";

        if (!File.Exists(backup) || !File.Exists(record))
            return Failed("Deployment-Backup oder Deployment-Metadaten fehlen. Automatischer Rollback wird verweigert.");
        if (File.Exists(patchedAuditBackup))
            return Failed("Rollback-Auditbackup existiert bereits; automatisches Überschreiben wird verweigert.");

        ZonePoolSingleZoneDeploymentMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<ZonePoolSingleZoneDeploymentMetadata>(File.ReadAllBytes(record));
        }
        catch (Exception ex)
        {
            return Failed("Deployment-Metadaten konnten nicht gelesen werden: " + ex.Message);
        }

        if (metadata is null
            || !string.Equals(metadata.State, "DEPLOYED", StringComparison.Ordinal)
            || !PathEquals(metadata.TargetPath, target)
            || !PathEquals(metadata.BackupPath, backup)
            || metadata.PlayerCapacity != ZonePoolOfflineWriterSelfTest.PlayerTarget
            || metadata.MobCapacity != ZonePoolOfflineWriterSelfTest.MobTarget
            || metadata.NpcCapacity != ZonePoolOfflineWriterSelfTest.NpcTarget
            || metadata.VerifiedSiteCount != 83)
        {
            return Failed("Deployment-Metadaten entsprechen nicht dem erwarteten Ein-Zonen-Testzustand.");
        }

        var currentHash = TrySha256File(target, out var currentHashError);
        if (currentHashError is not null)
            return Failed("Aktuelle Ziel-Zone.exe konnte nicht gehasht werden: " + currentHashError);
        var backupHash = TrySha256File(backup, out var backupHashError);
        if (backupHashError is not null)
            return Failed("Baseline-Backup konnte nicht gehasht werden: " + backupHashError);

        if (!currentHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase)
            || !currentHash.Equals(metadata.PatchedSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Aktuelle Ziel-Zone.exe entspricht nicht exakt dem registrierten Patch-SHA. Rollback verweigert.");
        if (!backupHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase)
            || !backupHash.Equals(metadata.BaselineSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Deployment-Backup entspricht nicht exakt dem registrierten Baseline-SHA. Rollback verweigert.");

        var processCheck = FindRunningZoneProcesses();
        if (!processCheck.ProcessEnumerationTrusted)
            return Failed("Zone-Prozessstatus ist nicht zuverlässig prüfbar. Rollback fail-closed abgebrochen.");
        if (processCheck.ProcessIds.Count > 0)
            return Failed("BLOCKIERT: Mindestens ein Zone-Prozess läuft: " + string.Join(", ", processCheck.ProcessIds.Select(x => "PID " + x)));

        var stage = target + ".nextgen-rollback-stage-" + Guid.NewGuid().ToString("N");
        var recordTemp = record + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteDurable(stage, File.ReadAllBytes(backup));
            if (!Sha256File(stage).Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Gestagte Baseline bestand die SHA-256-Prüfung nicht.");

            if (!Sha256File(target).Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Ziel-Zone.exe änderte sich während des Rollback-Stagings.");

            processCheck = FindRunningZoneProcesses();
            if (!processCheck.ProcessEnumerationTrusted)
                throw new IOException("Zone-Prozessstatus ist unmittelbar vor dem Rollback nicht zuverlässig prüfbar.");
            if (processCheck.ProcessIds.Count > 0)
                throw new IOException("Zone-Prozess wurde während des Rollback-Stagings gestartet: " + string.Join(", ", processCheck.ProcessIds.Select(x => "PID " + x)));

            File.Replace(stage, target, patchedAuditBackup, ignoreMetadataErrors: false);

            var restoredHash = Sha256File(target);
            var auditHash = Sha256File(patchedAuditBackup);
            if (!restoredHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Rollback-Ziel entspricht nicht dem Baseline-SHA.");
            if (!auditHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Rollback-Auditbackup entspricht nicht dem Patch-SHA.");

            var rolledBack = new ZonePoolSingleZoneDeploymentMetadata
            {
                FormatVersion = metadata.FormatVersion,
                State = "ROLLED_BACK",
                DeployedUtc = metadata.DeployedUtc,
                RolledBackUtc = DateTimeOffset.UtcNow,
                TargetPath = metadata.TargetPath,
                PatchSourcePath = metadata.PatchSourcePath,
                BackupPath = metadata.BackupPath,
                RollbackPatchedBackupPath = patchedAuditBackup,
                BaselineSha256 = metadata.BaselineSha256,
                PatchedSha256 = metadata.PatchedSha256,
                VerifiedSiteCount = metadata.VerifiedSiteCount,
                PlayerCapacity = metadata.PlayerCapacity,
                MobCapacity = metadata.MobCapacity,
                NpcCapacity = metadata.NpcCapacity
            };
            WriteJsonDurable(recordTemp, rolledBack);
            File.Move(recordTemp, record, overwrite: true);

            return new ZonePoolSingleZoneTestDeploymentResult
            {
                Success = true,
                RolledBack = true,
                TargetPath = target,
                BackupPath = backup,
                DeploymentRecordPath = record,
                RollbackPatchedBackupPath = patchedAuditBackup,
                TargetSha256 = restoredHash,
                BackupSha256 = backupHash,
                Detail = "ROLLED BACK: Ziel-Zone.exe ist bytegenau wieder die verifizierte NA2016-Baseline. " +
                         "Die zuvor eingesetzte Patchversion wurde als Auditbackup erhalten. Es wurde KEIN Zone-Prozess gestartet."
            };
        }
        catch (Exception ex)
        {
            SafeDelete(stage);
            SafeDelete(recordTemp);
            return Failed("Rollback fehlgeschlagen: " + ex.Message, targetPath: target, backupPath: backup, recordPath: record);
        }
    }

    private static EmergencyRollbackResult TryEmergencyRollback(string target, string backup)
    {
        if (!File.Exists(target) || !File.Exists(backup))
            return new EmergencyRollbackResult(false, "NOTFALL-ROLLBACK NICHT MÖGLICH: Ziel oder Backup fehlt.");

        try
        {
            var processCheck = FindRunningZoneProcesses();
            if (!processCheck.ProcessEnumerationTrusted || processCheck.ProcessIds.Count > 0)
                return new EmergencyRollbackResult(false, "NOTFALL-ROLLBACK BLOCKIERT: Zone-Prozessstatus ist nicht sicher oder eine Zone läuft.");

            if (!Sha256File(backup).Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                return new EmergencyRollbackResult(false, "NOTFALL-ROLLBACK BLOCKIERT: Backup-Hash ist nicht die Baseline.");

            var stage = target + ".nextgen-emergency-rollback-" + Guid.NewGuid().ToString("N");
            try
            {
                WriteDurable(stage, File.ReadAllBytes(backup));
                if (!Sha256File(stage).Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Notfall-Staging bestand die Hashprüfung nicht.");
                File.Replace(stage, target, null, ignoreMetadataErrors: false);
                if (!Sha256File(target).Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Notfall-Rollback bestand die finale Hashprüfung nicht.");
                return new EmergencyRollbackResult(true, "NOTFALL-ROLLBACK ERFOLGREICH: Ziel ist wieder die Baseline.");
            }
            finally
            {
                SafeDelete(stage);
            }
        }
        catch (Exception ex)
        {
            return new EmergencyRollbackResult(false, "NOTFALL-ROLLBACK FEHLGESCHLAGEN: " + ex.Message);
        }
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

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string TrySha256File(string path, out string? error)
    {
        try
        {
            error = null;
            return Sha256File(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return string.Empty;
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteDurable(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.WriteThrough);
        stream.Write(content, 0, content.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteJsonDurable(string path, ZonePoolSingleZoneDeploymentMetadata metadata)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true });
        WriteDurable(path, json);
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static ZonePoolSingleZoneTestDeploymentResult Failed(
        string detail,
        ZonePoolDeploymentPreflightResult? preflight = null,
        bool emergencyRollbackAttempted = false,
        bool emergencyRollbackSucceeded = false,
        string targetPath = "",
        string backupPath = "",
        string recordPath = "")
        => new()
        {
            Success = false,
            Preflight = preflight,
            EmergencyRollbackAttempted = emergencyRollbackAttempted,
            EmergencyRollbackSucceeded = emergencyRollbackSucceeded,
            TargetPath = targetPath,
            BackupPath = backupPath,
            DeploymentRecordPath = recordPath,
            Detail = detail
        };

    private sealed record ProcessInventory(bool ProcessEnumerationTrusted, IReadOnlyList<int> ProcessIds);
    private sealed record EmergencyRollbackResult(bool Success, string Detail);
}

public sealed class ZonePoolSingleZoneDeploymentMetadata
{
    public int FormatVersion { get; init; }
    public string State { get; init; } = string.Empty;
    public DateTimeOffset DeployedUtc { get; init; }
    public DateTimeOffset? RolledBackUtc { get; init; }
    public string TargetPath { get; init; } = string.Empty;
    public string PatchSourcePath { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string RollbackPatchedBackupPath { get; init; } = string.Empty;
    public string BaselineSha256 { get; init; } = string.Empty;
    public string PatchedSha256 { get; init; } = string.Empty;
    public int VerifiedSiteCount { get; init; }
    public int PlayerCapacity { get; init; }
    public int MobCapacity { get; init; }
    public int NpcCapacity { get; init; }
}

public sealed class ZonePoolSingleZoneTestDeploymentResult
{
    public bool Success { get; init; }
    public bool Deployed { get; init; }
    public bool RolledBack { get; init; }
    public bool EmergencyRollbackAttempted { get; init; }
    public bool EmergencyRollbackSucceeded { get; init; }
    public ZonePoolDeploymentPreflightResult? Preflight { get; init; }
    public string TargetPath { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string DeploymentRecordPath { get; init; } = string.Empty;
    public string RollbackPatchedBackupPath { get; init; } = string.Empty;
    public string TargetSha256 { get; init; } = string.Empty;
    public string BackupSha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
