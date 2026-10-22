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
    public const string ReapplyConfirmationToken = "REAPPLY-CERTIFIED-ZONE-POOL-2000-12000-512";

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
        if (File.Exists(patchedAuditBackup))
            patchedAuditBackup = target + ".nextgen-rollback-patched-" + Guid.NewGuid().ToString("N") + ".bak";

        if (!File.Exists(backup) || !File.Exists(record))
            return Failed("Deployment-Backup oder Deployment-Metadaten fehlen. Automatischer Rollback wird verweigert.");
        ZonePoolSingleZoneDeploymentMetadata? metadata;
        byte[] originalMetadata;
        try
        {
            originalMetadata = File.ReadAllBytes(record);
            metadata = JsonSerializer.Deserialize<ZonePoolSingleZoneDeploymentMetadata>(originalMetadata);
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
        var replaced = false;
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
            replaced = true;

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
            var recovery = replaced
                ? ZoneRollbackRecovery.RestorePreviousFile(target, patchedAuditBackup,
                    currentHash, backupHash, record, originalMetadata)
                : new ZoneRollbackRecovery.Result(false, "Binary wurde nicht ausgetauscht.");
            return Failed("Rollback fehlgeschlagen: " + ex.Message + " " + recovery.Detail,
                emergencyRollbackAttempted: replaced, emergencyRollbackSucceeded: recovery.Success,
                targetPath: target, backupPath: backup, recordPath: record);
        }
    }


    /// <summary>
    /// Restore the EXACT certified executable after a registered ROLLED_BACK cycle.
    /// Does not delete/overwrite prior audit files; maintains the original baseline
    /// backup and records the new deployed state for the existing strict runtime observer.
    /// Requires ALL Zone processes to be stopped.
    /// </summary>
    public ZonePoolSingleZoneTestDeploymentResult ReapplyRolledBack(
        string targetZoneExePath, string confirmationToken)
    {
        if (confirmationToken != ReapplyConfirmationToken)
            return Failed("Expliziter Wiederherstellungs-Token fehlt. Nichts geändert.");
        if (string.IsNullOrWhiteSpace(targetZoneExePath) || !File.Exists(targetZoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.");

        var target = Path.GetFullPath(targetZoneExePath);
        var backup = target + ".nextgen-prepatch.bak";
        var record = target + ".nextgen-deployment.json";
        if (!File.Exists(backup) || !File.Exists(record))
            return Failed("Verifiziertes Original-Backup oder Deployment-Record fehlen.");

        ZonePoolSingleZoneDeploymentMetadata? metadata;
        try { metadata = JsonSerializer.Deserialize<ZonePoolSingleZoneDeploymentMetadata>(File.ReadAllBytes(record)); }
        catch (Exception ex) { return Failed("Deployment-Record nicht lesbar: " + ex.Message); }

        var audit = metadata?.RollbackPatchedBackupPath;
        if (metadata is null || metadata.FormatVersion != 1 || metadata.State != "ROLLED_BACK"
            || !PathEquals(metadata.TargetPath, target)
            || !PathEquals(metadata.BackupPath, backup)
            || string.IsNullOrWhiteSpace(audit)
            || !Path.GetFullPath(audit).StartsWith(target + ".nextgen-rollback-patched", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(audit)
            || metadata.VerifiedSiteCount != 83
            || metadata.PlayerCapacity != ZonePoolOfflineWriterSelfTest.PlayerTarget
            || metadata.MobCapacity != ZonePoolOfflineWriterSelfTest.MobTarget
            || metadata.NpcCapacity != ZonePoolOfflineWriterSelfTest.NpcTarget
            || !metadata.BaselineSha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase)
            || !metadata.PatchedSha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("ROLLED_BACK-Record, Patch-Auditbackup oder Zielpfad passen nicht zum zertifizierten Test.");

        var baseline = ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256;
        var patched = ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256;
        if (Sha256File(target) != baseline || Sha256File(backup) != baseline
            || Sha256File(audit) != patched)
            return Failed("Original-Zone, Baseline-Backup oder Patch-Auditbackup haben falsche Hashes.");

        var verification = _patchedCopyVerifier.VerifyAgainstBaseline(audit, backup);
        if (!verification.Success || verification.VerifiedSiteCount != 83
            || !verification.PatchedSha256.Equals(patched, StringComparison.OrdinalIgnoreCase))
            return Failed("Audit-Patch bestand die unabhängige 83-Site-Prüfung nicht: " + verification.Detail);

        var processes = FindRunningZoneProcesses();
        if (!processes.ProcessEnumerationTrusted || processes.ProcessIds.Count != 0)
            return Failed("Wiederherstellung blockiert: Alle Zone-Prozesse müssen beendet und prüfbar sein.");

        var stage = target + ".nextgen-reapply-stage-" + Guid.NewGuid().ToString("N");
        var stockAudit = target + ".nextgen-reapply-stock-" + Guid.NewGuid().ToString("N") + ".bak";
        var recordTemp = record + ".tmp-" + Guid.NewGuid().ToString("N");
        var recordHistory = record + ".audit-" + Guid.NewGuid().ToString("N") + ".json";
        var replaced = false;
        try
        {
            WriteDurable(stage, File.ReadAllBytes(audit));
            if (Sha256File(stage) != patched || Sha256File(target) != baseline)
                throw new IOException("Binary änderte sich nach dem Hash-Preflight.");
            processes = FindRunningZoneProcesses();
            if (!processes.ProcessEnumerationTrusted || processes.ProcessIds.Count != 0)
                throw new IOException("Zone-Prozess ist während des Stagings gestartet.");

            // Preserve this cycle's stock state separately. Never overwrite backup/history.
            File.Replace(stage, target, stockAudit, ignoreMetadataErrors: false);
            replaced = true;
            if (Sha256File(target) != patched || Sha256File(stockAudit) != baseline)
                throw new IOException("Nach dem Austausch stimmen Ziel-/Stock-Audithashes nicht.");

            var restoredMetadata = new ZonePoolSingleZoneDeploymentMetadata
            {
                FormatVersion = metadata.FormatVersion,
                State = "DEPLOYED",
                DeployedUtc = DateTimeOffset.UtcNow,
                TargetPath = metadata.TargetPath,
                PatchSourcePath = metadata.PatchSourcePath,
                BackupPath = metadata.BackupPath,
                RollbackPatchedBackupPath = metadata.RollbackPatchedBackupPath,
                BaselineSha256 = metadata.BaselineSha256,
                PatchedSha256 = metadata.PatchedSha256,
                VerifiedSiteCount = metadata.VerifiedSiteCount,
                PlayerCapacity = metadata.PlayerCapacity,
                MobCapacity = metadata.MobCapacity,
                NpcCapacity = metadata.NpcCapacity
            };
            WriteJsonDurable(recordTemp, restoredMetadata);
            File.Replace(recordTemp, record, recordHistory, ignoreMetadataErrors: false);
            return new ZonePoolSingleZoneTestDeploymentResult
            {
                Success = true,
                Deployed = true,
                TargetPath = target,
                BackupPath = backup,
                DeploymentRecordPath = record,
                RollbackPatchedBackupPath = audit,
                TargetSha256 = patched,
                BackupSha256 = baseline,
                Detail = "REAPPLY SUCCESS: zertifizierte Zone.exe 2000/12000/512 bytegenau wiederhergestellt. " +
                         "Original, Patch-Audit, Stock-Zwischenbackup und Record-Historie erhalten. " +
                         "Listener muss danach separat wieder auf 2000 gesetzt werden."
            };
        }
        catch (Exception ex)
        {
            var emergency = replaced ? TryEmergencyRollback(target, backup) : new EmergencyRollbackResult(false, "Binary nicht ausgetauscht.");
            return Failed("REAPPLY fehlgeschlagen: " + ex.Message + " " + emergency.Detail,
                emergencyRollbackAttempted: replaced,
                emergencyRollbackSucceeded: emergency.Success,
                targetPath: target, backupPath: backup, recordPath: record);
        }
        finally
        {
            SafeDelete(stage);
            SafeDelete(recordTemp);
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
