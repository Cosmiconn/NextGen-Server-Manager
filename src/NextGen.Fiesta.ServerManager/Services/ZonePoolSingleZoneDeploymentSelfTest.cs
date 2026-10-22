using System.Security.Cryptography;
using System.Text.Json;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// End-to-end filesystem self-test for the certified single-Zone deployment transaction.
/// It operates exclusively in a fresh temporary directory and never replaces the supplied
/// baseline file. No Zone process may be running because the real production guards are used.
/// </summary>
public sealed class ZonePoolSingleZoneDeploymentSelfTest
{
    private readonly ZonePoolOfflinePatchWriter _writer = new();
    private readonly ZonePoolSingleZoneTestDeployment _deployment = new();

    public ZonePoolSingleZoneDeploymentSelfTestResult Run(string baselineZoneExePath)
    {
        if (string.IsNullOrWhiteSpace(baselineZoneExePath) || !File.Exists(baselineZoneExePath))
            return Failed("Baseline-Zone.exe wurde nicht gefunden.");

        var source = Path.GetFullPath(baselineZoneExePath);
        var sourceHashBefore = Sha256File(source);
        if (!sourceHashBefore.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Baseline-Hash stimmt nicht: " + sourceHashBefore);

        var testDirectory = Path.Combine(Path.GetTempPath(), "NextGen-ZonePoolDeploymentSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var completedSuccessfully = false;

        var root = Path.Combine(testDirectory, "Server");
        var zoneFolder = Path.Combine(root, "Zone00");
        var infoFolder = Path.Combine(root, "9Data", "ServerInfo");
        Directory.CreateDirectory(zoneFolder);
        Directory.CreateDirectory(infoFolder);
        var target = Path.Combine(zoneFolder, "Zone.exe");
        var patched = Path.Combine(testDirectory, "Zone.NextGen-2000-12000-512.exe");
        var info = Path.Combine(infoFolder, "ServerInfo.txt");
        const string stockServerInfo = "#DEFINE SERVER_INFO\r\n" +
            "SERVER_INFO  \"PG_W00_Z00\", 6, 0, 0,20, \"127.0.0.1\", 9016, 100, 1500  ; PUBLIC_IP\r\n" +
            "SERVER_INFO  \"PG_W00_Z00\", 6, 0, 0, 6, \"127.0.0.1\", 9017,   1,   30  ; LOCALHOST\r\n" +
            "SERVER_INFO  \"PG_W00_Z01\", 6, 0, 1,20, \"127.0.0.1\", 9019, 100, 1500  ; PUBLIC_IP\r\n";
        File.WriteAllText(info, stockServerInfo, System.Text.Encoding.Latin1);

        try
        {
            File.Copy(source, target, overwrite: false);
            if (!Sha256File(target).Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                return Failed("Temporäre Zielkopie entspricht nicht der Baseline.", testDirectory);

            var create = _writer.CreatePatchedCopy(
                source,
                patched,
                ZonePoolOfflineWriterSelfTest.PlayerTarget,
                ZonePoolOfflineWriterSelfTest.MobTarget,
                ZonePoolOfflineWriterSelfTest.NpcTarget);
            if (!create.Success)
                return Failed("Offline-Patchkopie konnte nicht erzeugt werden: " + create.Detail, testDirectory, create);

            var deploy = _deployment.Deploy(
                target,
                patched,
                ZonePoolSingleZoneTestDeployment.DeploymentConfirmationToken);
            if (!deploy.Success || !deploy.Deployed)
                return Failed("Temporäres Deployment fehlgeschlagen: " + deploy.Detail, testDirectory, create, deploy);

            var deployedHash = Sha256File(target);
            if (!deployedHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
                return Failed("Temporäres Ziel besitzt nach Deployment nicht den gepinnten Patch-SHA.", testDirectory, create, deploy);

            var deploymentBackup = target + ".nextgen-prepatch.bak";
            var deploymentRecord = target + ".nextgen-deployment.json";
            if (!File.Exists(deploymentBackup) || !File.Exists(deploymentRecord))
                return Failed("Deployment-Backup oder Deployment-JSON fehlt nach erfolgreichem Austausch.", testDirectory, create, deploy);
            if (!Sha256File(deploymentBackup).Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                return Failed("Deployment-Backup besitzt nicht den Baseline-SHA.", testDirectory, create, deploy);

            var deployedMetadata = ReadMetadata(deploymentRecord);
            if (deployedMetadata is null
                || !string.Equals(deployedMetadata.State, "DEPLOYED", StringComparison.Ordinal)
                || deployedMetadata.VerifiedSiteCount != 83
                || !deployedMetadata.BaselineSha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase)
                || !deployedMetadata.PatchedSha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failed("Deployment-JSON entspricht nicht dem erwarteten DEPLOYED-Zustand.", testDirectory, create, deploy);
            }

            var listenerService = new ZoneClientListenerTestConfiguration();
            var listenerInitial = listenerService.Apply(
                target, ZoneClientListenerTestConfiguration.ApplyConfirmationToken);
            if (!listenerInitial.Success || !listenerInitial.Applied)
                return Failed("Isolierter 2000-Listener-Apply fehlgeschlagen: " + listenerInitial.Detail, testDirectory, create, deploy);
            if (!listenerService.VerifyApplied(target).Verified)
                return Failed("Isolierter Listener nicht als 2000 verifiziert.", testDirectory, create, deploy);
            // A reader that does not share DELETE lets validation read the metadata,
            // but makes its final atomic replacement fail after the data-file exchange.
            var listenerRecordBefore = File.ReadAllBytes(listenerInitial.ManifestPath);
            using (var lockedManifest = new FileStream(listenerInitial.ManifestPath,
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var rejectedRollback = listenerService.Rollback(
                    target, ZoneClientListenerTestConfiguration.RollbackConfirmationToken);
                if (rejectedRollback.Success || !rejectedRollback.EmergencyRollbackAttempted
                    || !rejectedRollback.EmergencyRollbackSucceeded
                    || Sha256File(info) != listenerInitial.AppliedSha256
                    || !File.ReadAllBytes(listenerInitial.ManifestPath).SequenceEqual(listenerRecordBefore))
                    return Failed("Gesperrtes Listener-Manifest hinterließ einen inkonsistenten Zustand: " +
                        rejectedRollback.Detail, testDirectory, create, deploy);
            }
            if (!listenerService.VerifyApplied(target).Verified)
                return Failed("Listener nach kompensiertem Rollback nicht mehr verifiziert.", testDirectory, create, deploy);

            var listenerRollback = listenerService.Rollback(
                target, ZoneClientListenerTestConfiguration.RollbackConfirmationToken);
            if (!listenerRollback.Success || !listenerRollback.RolledBack ||
                File.ReadAllText(info, System.Text.Encoding.Latin1) != stockServerInfo)
                return Failed("Isolierter Listener-Rollback auf Stock scheiterte.", testDirectory, create, deploy);

            var deploymentRecordBefore = File.ReadAllBytes(deploymentRecord);
            using (var lockedRecord = new FileStream(deploymentRecord,
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var rejectedRollback = _deployment.Rollback(
                    target, ZonePoolSingleZoneTestDeployment.RollbackConfirmationToken);
                if (rejectedRollback.Success || !rejectedRollback.EmergencyRollbackAttempted
                    || !rejectedRollback.EmergencyRollbackSucceeded
                    || Sha256File(target) != ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256
                    || !File.ReadAllBytes(deploymentRecord).SequenceEqual(deploymentRecordBefore))
                    return Failed("Gesperrter Binary-Record hinterließ einen inkonsistenten Zustand: " +
                        rejectedRollback.Detail, testDirectory, create, deploy);
            }

            var rollback = _deployment.Rollback(
                target,
                ZonePoolSingleZoneTestDeployment.RollbackConfirmationToken);
            if (!rollback.Success || !rollback.RolledBack)
                return Failed("Temporärer Rollback fehlgeschlagen: " + rollback.Detail, testDirectory, create, deploy, rollback);

            var rolledBackHash = Sha256File(target);
            if (!rolledBackHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                return Failed("Temporäres Ziel ist nach Rollback nicht bytegenau die Baseline.", testDirectory, create, deploy, rollback);

            var patchAuditBackup = rollback.RollbackPatchedBackupPath;
            if (!File.Exists(patchAuditBackup)
                || !Sha256File(patchAuditBackup).Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failed("Rollback-Auditbackup fehlt oder besitzt nicht den Patch-SHA.", testDirectory, create, deploy, rollback);
            }

            var rolledBackMetadata = ReadMetadata(deploymentRecord);
            if (rolledBackMetadata is null
                || !string.Equals(rolledBackMetadata.State, "ROLLED_BACK", StringComparison.Ordinal)
                || rolledBackMetadata.RolledBackUtc is null
                || string.IsNullOrWhiteSpace(rolledBackMetadata.RollbackPatchedBackupPath))
            {
                return Failed("Deployment-JSON entspricht nach Rollback nicht dem erwarteten ROLLED_BACK-Zustand.", testDirectory, create, deploy, rollback);
            }


            // Real end-to-end cycle: Stock (rollback) -> certified binary reapply ->
            // exact listener 2000 reapply -> second full rollback. Assert every
            // original audit remains intact and the second audit uses a new name.
            var denied = _deployment.ReapplyRolledBack(target, "wrong-confirmation");
            if (denied.Success || Sha256File(target) != ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256)
                return Failed("Reapply ohne explizite Freigabe darf kein Binary verändern.", testDirectory, create, deploy, rollback);

            // Rollback archives intentionally have no offline-writer sidecars.
            // Verify from the registered baseline, while rejecting damage to either image.
            var verifier = new ZonePoolPatchedCopyVerifier();
            var archiveVerification = verifier.VerifyAgainstBaseline(patchAuditBackup, deploymentBackup);
            if (!archiveVerification.Success || archiveVerification.VerifiedSiteCount != 83
                || verifier.Verify(patchAuditBackup).Success)
                return Failed("Archivprüfung muss ohne Writer-Sidecars 83 Sites belegen; Writer-Prüfung bleibt strikt.",
                    testDirectory, create, deploy, rollback);

            var archivedBytes = File.ReadAllBytes(patchAuditBackup);
            var damagedBytes = archivedBytes.ToArray();
            damagedBytes[^1] ^= 1;
            File.WriteAllBytes(patchAuditBackup, damagedBytes);
            var rejected = _deployment.ReapplyRolledBack(
                target, ZonePoolSingleZoneTestDeployment.ReapplyConfirmationToken);
            if (rejected.Success || verifier.VerifyAgainstBaseline(patchAuditBackup, deploymentBackup).Success
                || Sha256File(target) != ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256
                || ReadMetadata(deploymentRecord)?.State != "ROLLED_BACK")
                return Failed("Beschädigtes Patcharchiv wurde akzeptiert oder änderte den Stockzustand.",
                    testDirectory, create, deploy, rollback);
            File.WriteAllBytes(patchAuditBackup, archivedBytes);

            var baselineBytes = File.ReadAllBytes(deploymentBackup);
            damagedBytes = baselineBytes.ToArray();
            damagedBytes[^1] ^= 1;
            File.WriteAllBytes(deploymentBackup, damagedBytes);
            rejected = _deployment.ReapplyRolledBack(
                target, ZonePoolSingleZoneTestDeployment.ReapplyConfirmationToken);
            if (rejected.Success || verifier.VerifyAgainstBaseline(patchAuditBackup, deploymentBackup).Success
                || Sha256File(target) != ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256
                || ReadMetadata(deploymentRecord)?.State != "ROLLED_BACK")
                return Failed("Beschädigtes Baseline-Backup wurde akzeptiert oder änderte den Stockzustand.",
                    testDirectory, create, deploy, rollback);
            File.WriteAllBytes(deploymentBackup, baselineBytes);
            if (verifier.VerifyAgainstBaseline(target, deploymentBackup).Success
                || verifier.VerifyAgainstBaseline(patchAuditBackup, deploymentBackup + ".missing").Success)
                return Failed("Stock als Patch oder fehlendes Originalbackup wurde akzeptiert.",
                    testDirectory, create, deploy, rollback);

            var binaryReapply = _deployment.ReapplyRolledBack(
                target, ZonePoolSingleZoneTestDeployment.ReapplyConfirmationToken);
            if (!binaryReapply.Success || !binaryReapply.Deployed ||
                Sha256File(target) != ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256)
                return Failed("Zertifizierte Binary-Wiederherstellung fehlgeschlagen: " + binaryReapply.Detail,
                    testDirectory, create, deploy, rollback);
            var listenerReapply = listenerService.ReapplyRolledBack(
                target, ZoneClientListenerTestConfiguration.ReapplyConfirmationToken);
            if (!listenerReapply.Success || !listenerReapply.Applied || !listenerService.VerifyApplied(target).Verified)
                return Failed("Zertifizierte Listener-Wiederherstellung fehlgeschlagen: " + listenerReapply.Detail,
                    testDirectory, create, deploy, rollback);
            if (!File.Exists(patchAuditBackup) ||
                Sha256File(patchAuditBackup) != ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256)
                return Failed("Erstes Patch-Audit wurde beim Reapply verändert.", testDirectory, create, deploy, rollback);

            var listenerSecondRollback = listenerService.Rollback(
                target, ZoneClientListenerTestConfiguration.RollbackConfirmationToken);
            if (!listenerSecondRollback.Success || !listenerSecondRollback.RolledBack ||
                File.ReadAllText(info, System.Text.Encoding.Latin1) != stockServerInfo)
                return Failed("Zweiter Listener-Rollback fehlgeschlagen.", testDirectory, create, deploy, rollback);
            var secondRollback = _deployment.Rollback(
                target, ZonePoolSingleZoneTestDeployment.RollbackConfirmationToken);
            var secondMetadata = ReadMetadata(deploymentRecord);
            if (!secondRollback.Success || !secondRollback.RolledBack ||
                Sha256File(target) != ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256 ||
                secondMetadata?.State != "ROLLED_BACK" ||
                secondMetadata.RollbackPatchedBackupPath == patchAuditBackup ||
                !File.Exists(secondMetadata.RollbackPatchedBackupPath) ||
                Sha256File(secondMetadata.RollbackPatchedBackupPath) != ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256 ||
                !File.Exists(patchAuditBackup) ||
                Sha256File(patchAuditBackup) != ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256)
                return Failed("Zweiter Binary-Rollback verlor die historisierten Patchbackups: " + secondRollback.Detail,
                    testDirectory, create, deploy, rollback);
            var firstListenerAudit = listenerRollback.AppliedAuditBackupPath;
            var secondListenerAudit = listenerSecondRollback.AppliedAuditBackupPath;
            if (firstListenerAudit == secondListenerAudit
                || !File.Exists(firstListenerAudit) || !File.Exists(secondListenerAudit)
                || Sha256File(firstListenerAudit) != listenerInitial.AppliedSha256
                || Sha256File(secondListenerAudit) != listenerInitial.AppliedSha256)
                return Failed("Listener-Audits wurden beim zweiten Zyklus verändert oder überschrieben.",
                    testDirectory, create, deploy, rollback);

            // Exercise another cycle from the legacy second-rollback filename:
            // previous versions emitted this name but rejected it on reapply.
            var legacyAudit = info + ".nextgen-zone-listener-rollback-applied-" + Guid.NewGuid().ToString("N") + ".bak";
            File.Copy(secondListenerAudit, legacyAudit);
            var listenerRecord = listenerSecondRollback.ManifestPath;
            var listenerMetadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(listenerRecord))!;
            listenerMetadata["AppliedAuditBackupPath"] = legacyAudit;
            File.WriteAllText(listenerRecord, listenerMetadata.ToJsonString());
            var thirdBinary = _deployment.ReapplyRolledBack(target, ZonePoolSingleZoneTestDeployment.ReapplyConfirmationToken);
            var thirdListener = listenerService.ReapplyRolledBack(target, ZoneClientListenerTestConfiguration.ReapplyConfirmationToken);
            if (!thirdBinary.Success || !thirdListener.Success || !listenerService.VerifyApplied(target).Verified)
                return Failed("Dritter Reapply aus bestehender Listener-Audithistorie scheiterte: " +
                    thirdBinary.Detail + " " + thirdListener.Detail, testDirectory, create, deploy, rollback);
            var thirdListenerRollback = listenerService.Rollback(target, ZoneClientListenerTestConfiguration.RollbackConfirmationToken);
            var thirdBinaryRollback = _deployment.Rollback(target, ZonePoolSingleZoneTestDeployment.RollbackConfirmationToken);
            if (!thirdListenerRollback.Success || !thirdBinaryRollback.Success
                || File.ReadAllText(info, System.Text.Encoding.Latin1) != stockServerInfo
                || Sha256File(target) != ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256
                || Sha256File(firstListenerAudit) != listenerInitial.AppliedSha256
                || Sha256File(secondListenerAudit) != listenerInitial.AppliedSha256
                || Sha256File(legacyAudit) != listenerInitial.AppliedSha256)
                return Failed("Dritter Rollback oder Erhalt der Listener-Historie scheiterte.",
                    testDirectory, create, deploy, rollback);

            var sourceHashAfter = Sha256File(source);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                return Failed("Die vom Benutzer angegebene Baseline-Datei wurde während des Selbsttests verändert.", testDirectory, create, deploy, rollback);

            completedSuccessfully = true;
            return new ZonePoolSingleZoneDeploymentSelfTestResult
            {
                Success = true,
                CreateResult = create,
                DeployResult = deploy,
                RollbackResult = rollback,
                BaselineSha256 = sourceHashAfter,
                DeployedSha256 = deployedHash,
                RolledBackSha256 = rolledBackHash,
                TemporaryDirectory = testDirectory,
                Detail = "SELFTEST OK: isolierte Baseline-Kopie -> zertifizierte Patchkopie -> atomarer Ein-Zonen-Austausch -> " +
                         "Backup/JSON/Listener 2000 -> Stock-Rollback -> identisch verifizierter Binary+Listener-Reapply -> " +
                         "dritter Stock-Rollback; Patch-/Listener-Historie erhalten, beschädigte Dateien abgelehnt, gesperrte Metadaten konsistent kompensiert, Original unverändert."
            };
        }
        catch (Exception ex)
        {
            return Failed("Deployment-Selbsttest-Ausnahme: " + ex.Message, testDirectory);
        }
        finally
        {
            try
            {
                // Retain the isolated files on FAILURE for hash/manifest/audit inspection.
                // Successful runs still remove their disposable test directory.
                if (completedSuccessfully && Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, recursive: true);
            }
            catch
            {
                // A successful test may leave its temp directory if Windows holds a handle.
                // Cleanup failure does not change the verified test verdict.
            }
        }
    }

    private static ZonePoolSingleZoneDeploymentMetadata? ReadMetadata(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ZonePoolSingleZoneDeploymentMetadata>(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ZonePoolSingleZoneDeploymentSelfTestResult Failed(
        string detail,
        string directory = "",
        ZonePoolOfflinePatchWriteResult? create = null,
        ZonePoolSingleZoneTestDeploymentResult? deploy = null,
        ZonePoolSingleZoneTestDeploymentResult? rollback = null)
        => new()
        {
            Success = false,
            CreateResult = create,
            DeployResult = deploy,
            RollbackResult = rollback,
            TemporaryDirectory = directory,
            Detail = detail
        };
}

public sealed class ZonePoolSingleZoneDeploymentSelfTestResult
{
    public bool Success { get; init; }
    public ZonePoolOfflinePatchWriteResult? CreateResult { get; init; }
    public ZonePoolSingleZoneTestDeploymentResult? DeployResult { get; init; }
    public ZonePoolSingleZoneTestDeploymentResult? RollbackResult { get; init; }
    public string BaselineSha256 { get; init; } = string.Empty;
    public string DeployedSha256 { get; init; } = string.Empty;
    public string RolledBackSha256 { get; init; } = string.Empty;
    public string TemporaryDirectory { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
