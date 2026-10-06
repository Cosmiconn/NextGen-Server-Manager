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

        var target = Path.Combine(testDirectory, "Zone.exe");
        var patched = Path.Combine(testDirectory, "Zone.NextGen-2000-12000-512.exe");

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

            var rollback = _deployment.Rollback(
                target,
                ZonePoolSingleZoneTestDeployment.RollbackConfirmationToken);
            if (!rollback.Success || !rollback.RolledBack)
                return Failed("Temporärer Rollback fehlgeschlagen: " + rollback.Detail, testDirectory, create, deploy, rollback);

            var rolledBackHash = Sha256File(target);
            if (!rolledBackHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                return Failed("Temporäres Ziel ist nach Rollback nicht bytegenau die Baseline.", testDirectory, create, deploy, rollback);

            var patchAuditBackup = target + ".nextgen-rollback-patched.bak";
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

            var sourceHashAfter = Sha256File(source);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                return Failed("Die vom Benutzer angegebene Baseline-Datei wurde während des Selbsttests verändert.", testDirectory, create, deploy, rollback);

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
                         "Backup/JSON-Prüfung -> atomarer Rollback. Ziel wieder Baseline, Patch als Auditbackup erhalten, Original unverändert."
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
                if (Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, recursive: true);
            }
            catch
            {
                // The result includes the temp directory for manual cleanup if Windows
                // still holds a handle briefly. Cleanup failure does not change the verdict.
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
