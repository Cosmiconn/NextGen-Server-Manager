using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Transactional ServerInfo configuration for the ONE-Zone 2000-player load test.
/// This service never patches a binary and never starts/stops a Zone process.
/// It only raises the selected Zone client listener from stock 1500 to 2000 when
/// the selected Zone.exe is already the independently certified 2000/12000/512 build.
/// </summary>
public sealed class ZoneClientListenerTestConfiguration
{
    public const string ApplyConfirmationToken = "ENABLE-CERTIFIED-ZONE-LISTENER-2000";
    public const string RollbackConfirmationToken = "ROLLBACK-CERTIFIED-ZONE-LISTENER-TEST";
    public const int StockMaxAccept = 1500;
    public const int CertifiedMaxAccept = 2000;

    private const int ZoneServerType = 6;
    private const int ZoneClientConnectionKind = 20;

    private static readonly Regex ZoneFolderRx = new(
        "^Zone(?<zone>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ServerInfoLineRx = new(
        "^(?<prefix>\\s*SERVER_INFO\\s+\"(?<name>[^\"]+)\"\\s*,\\s*(?<type>-?\\d+)\\s*,\\s*(?<world>-?\\d+)\\s*,\\s*(?<zone>-?\\d+)\\s*,\\s*(?<kind>-?\\d+)\\s*,\\s*\"(?<host>[^\"]+)\"\\s*,\\s*(?<port>\\d+)\\s*,\\s*(?<backlog>\\d+)\\s*,\\s*)(?<maxaccept>\\d+)(?<suffix>[^\\r\\n]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly ServerInfoParser _parser = new();

    public ZoneClientListenerConfigurationResult Apply(
        string targetZoneExePath,
        string confirmationToken)
    {
        if (!string.Equals(confirmationToken, ApplyConfirmationToken, StringComparison.Ordinal))
            return Failed("Expliziter Listener-Bestätigungstoken fehlt oder ist falsch. Es wurde nichts verändert.");

        var readiness = AnalyzeApplyReadiness(targetZoneExePath);
        if (!readiness.Ready)
            return Failed("Listener-Preflight ist nicht READY: " + readiness.Detail, readiness);

        var serverInfoPath = readiness.ServerInfoPath;
        var backupPath = GetBackupPath(serverInfoPath);
        var manifestPath = GetManifestPath(serverInfoPath);
        var stagePath = serverInfoPath + ".nextgen-zone-listener-stage-" + Guid.NewGuid().ToString("N");
        var manifestTemp = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var replaced = false;

        try
        {
            var originalBytes = File.ReadAllBytes(serverInfoPath);
            var originalText = Encoding.Latin1.GetString(originalBytes);
            var rewrite = RewriteTarget(originalText, readiness.TargetEntry!, CertifiedMaxAccept);
            if (!rewrite.Success || rewrite.ChangedCount != 1)
                throw new InvalidDataException(rewrite.Detail);

            var stagedBytes = Encoding.Latin1.GetBytes(rewrite.Text);
            WriteDurable(stagePath, stagedBytes);

            if (!Sha256Bytes(originalBytes).Equals(readiness.OriginalServerInfoSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("ServerInfo.txt änderte sich nach dem Preflight.");

            ValidateOnlyExpectedEntryChanged(serverInfoPath, stagePath, readiness.TargetEntry!, CertifiedMaxAccept);

            var processCheck = FindRunningZoneProcesses();
            if (!processCheck.ProcessEnumerationTrusted)
                throw new IOException("Zone-Prozessstatus ist unmittelbar vor der Konfigurationsänderung nicht zuverlässig prüfbar.");
            if (processCheck.ProcessIds.Count > 0)
                throw new IOException("Zone-Prozess wurde während des Stagings gestartet: " + FormatPids(processCheck.ProcessIds));

            if (File.Exists(backupPath) || File.Exists(manifestPath))
                throw new IOException("Listener-Backup oder Listener-Manifest wurde seit dem Preflight angelegt.");

            if (!Sha256File(readiness.TargetZoneExePath).Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Ziel-Zone.exe ist unmittelbar vor der Änderung nicht mehr der zertifizierte Patch-Build.");
            ValidateDeploymentMetadata(readiness.TargetZoneExePath);

            File.Replace(stagePath, serverInfoPath, backupPath, ignoreMetadataErrors: false);
            replaced = true;

            var originalHash = Sha256File(backupPath);
            var appliedHash = Sha256File(serverInfoPath);
            if (!originalHash.Equals(readiness.OriginalServerInfoSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Listener-Backup entspricht nach dem Austausch nicht dem gepinnten ServerInfo-SHA.");
            if (!appliedHash.Equals(Sha256Bytes(stagedBytes), StringComparison.OrdinalIgnoreCase))
                throw new IOException("ServerInfo.txt entspricht nach dem Austausch nicht dem verifizierten Staging-SHA.");

            ValidateOnlyExpectedEntryChanged(backupPath, serverInfoPath, readiness.TargetEntry!, CertifiedMaxAccept);

            var manifest = new ZoneClientListenerConfigurationMetadata
            {
                FormatVersion = 1,
                State = "DEPLOYED",
                AppliedUtc = DateTimeOffset.UtcNow,
                ServerInfoPath = serverInfoPath,
                BackupPath = backupPath,
                TargetZoneExePath = readiness.TargetZoneExePath,
                TargetEntryName = readiness.TargetEntry!.Name,
                WorldNo = readiness.TargetEntry.WorldNo,
                ZoneNo = readiness.TargetEntry.ZoneNo,
                OldMaxAccept = StockMaxAccept,
                NewMaxAccept = CertifiedMaxAccept,
                OriginalSha256 = originalHash,
                AppliedSha256 = appliedHash,
                CertifiedZoneSha256 = ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256
            };
            WriteJsonDurable(manifestTemp, manifest);
            File.Move(manifestTemp, manifestPath);

            return new ZoneClientListenerConfigurationResult
            {
                Success = true,
                Applied = true,
                Readiness = readiness,
                ServerInfoPath = serverInfoPath,
                BackupPath = backupPath,
                ManifestPath = manifestPath,
                OriginalSha256 = originalHash,
                AppliedSha256 = appliedHash,
                Detail = $"LISTENER TEST CONFIG: APPLIED. Nur {readiness.TargetEntry.Name} (World {readiness.TargetEntry.WorldNo}, Zone {readiness.TargetEntry.ZoneNo}) wurde von {StockMaxAccept:N0} auf {CertifiedMaxAccept:N0} Client-Sessions gesetzt. Backup und Manifest sind hashverifiziert. Es wurde KEIN Zone-Prozess gestartet."
            };
        }
        catch (Exception ex)
        {
            SafeDelete(stagePath);
            SafeDelete(manifestTemp);

            if (replaced)
            {
                var emergency = TryEmergencyRollback(serverInfoPath, backupPath, readiness.OriginalServerInfoSha256);
                return Failed(
                    "Listener-Konfiguration nach Dateiaustausch fehlgeschlagen: " + ex.Message + " " + emergency.Detail,
                    readiness,
                    emergencyRollbackAttempted: true,
                    emergencyRollbackSucceeded: emergency.Success,
                    serverInfoPath: serverInfoPath,
                    backupPath: backupPath,
                    manifestPath: manifestPath);
            }

            return Failed("Listener-Konfiguration vor dem Dateiaustausch abgebrochen: " + ex.Message, readiness);
        }
    }

    public ZoneClientListenerConfigurationResult Rollback(
        string targetZoneExePath,
        string confirmationToken)
    {
        if (!string.Equals(confirmationToken, RollbackConfirmationToken, StringComparison.Ordinal))
            return Failed("Expliziter Listener-Rollback-Bestätigungstoken fehlt oder ist falsch. Es wurde nichts verändert.");

        if (!TryResolvePaths(targetZoneExePath, out var targetZoneExe, out var serverInfoPath, out _, out var resolveError))
            return Failed(resolveError);

        var backupPath = GetBackupPath(serverInfoPath);
        var manifestPath = GetManifestPath(serverInfoPath);
        var appliedAuditBackupPath = GetAppliedAuditBackupPath(serverInfoPath);

        if (!File.Exists(serverInfoPath) || !File.Exists(backupPath) || !File.Exists(manifestPath))
            return Failed("Listener-ServerInfo, Backup oder Manifest fehlt. Automatischer Rollback wird verweigert.");
        if (File.Exists(appliedAuditBackupPath))
            return Failed("Listener-Rollback-Auditbackup existiert bereits; automatisches Überschreiben wird verweigert.");

        ZoneClientListenerConfigurationMetadata? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ZoneClientListenerConfigurationMetadata>(File.ReadAllBytes(manifestPath));
        }
        catch (Exception ex)
        {
            return Failed("Listener-Manifest konnte nicht gelesen werden: " + ex.Message);
        }

        if (manifest is null
            || manifest.FormatVersion != 1
            || !string.Equals(manifest.State, "DEPLOYED", StringComparison.Ordinal)
            || !PathEquals(manifest.ServerInfoPath, serverInfoPath)
            || !PathEquals(manifest.BackupPath, backupPath)
            || !PathEquals(manifest.TargetZoneExePath, targetZoneExe)
            || manifest.OldMaxAccept != StockMaxAccept
            || manifest.NewMaxAccept != CertifiedMaxAccept
            || !manifest.CertifiedZoneSha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failed("Listener-Manifest entspricht nicht dem erwarteten zertifizierten Ein-Zonen-Testzustand.");
        }

        var currentHash = TrySha256File(serverInfoPath, out var currentHashError);
        if (currentHashError is not null)
            return Failed("Aktuelle ServerInfo.txt konnte nicht gehasht werden: " + currentHashError);
        var backupHash = TrySha256File(backupPath, out var backupHashError);
        if (backupHashError is not null)
            return Failed("Listener-Backup konnte nicht gehasht werden: " + backupHashError);

        if (!currentHash.Equals(manifest.AppliedSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Aktuelle ServerInfo.txt entspricht nicht dem registrierten Listener-Testzustand. Rollback verweigert.");
        if (!backupHash.Equals(manifest.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Listener-Backup entspricht nicht dem registrierten Originalzustand. Rollback verweigert.");

        var processCheck = FindRunningZoneProcesses();
        if (!processCheck.ProcessEnumerationTrusted)
            return Failed("Zone-Prozessstatus ist nicht zuverlässig prüfbar. Listener-Rollback fail-closed abgebrochen.");
        if (processCheck.ProcessIds.Count > 0)
            return Failed("BLOCKIERT: Mindestens ein Zone-Prozess läuft: " + FormatPids(processCheck.ProcessIds));

        var stagePath = serverInfoPath + ".nextgen-zone-listener-rollback-stage-" + Guid.NewGuid().ToString("N");
        var manifestTemp = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteDurable(stagePath, File.ReadAllBytes(backupPath));
            if (!Sha256File(stagePath).Equals(manifest.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Gestagtes Listener-Backup bestand die SHA-256-Prüfung nicht.");

            if (!Sha256File(serverInfoPath).Equals(manifest.AppliedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("ServerInfo.txt änderte sich während des Rollback-Stagings.");

            processCheck = FindRunningZoneProcesses();
            if (!processCheck.ProcessEnumerationTrusted)
                throw new IOException("Zone-Prozessstatus ist unmittelbar vor dem Listener-Rollback nicht zuverlässig prüfbar.");
            if (processCheck.ProcessIds.Count > 0)
                throw new IOException("Zone-Prozess wurde während des Listener-Rollback-Stagings gestartet: " + FormatPids(processCheck.ProcessIds));

            File.Replace(stagePath, serverInfoPath, appliedAuditBackupPath, ignoreMetadataErrors: false);

            var restoredHash = Sha256File(serverInfoPath);
            var auditHash = Sha256File(appliedAuditBackupPath);
            if (!restoredHash.Equals(manifest.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Listener-Rollback-Ziel entspricht nicht dem Original-SHA.");
            if (!auditHash.Equals(manifest.AppliedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Listener-Rollback-Auditbackup entspricht nicht dem angewendeten SHA.");

            var rolledBack = new ZoneClientListenerConfigurationMetadata
            {
                FormatVersion = manifest.FormatVersion,
                State = "ROLLED_BACK",
                AppliedUtc = manifest.AppliedUtc,
                RolledBackUtc = DateTimeOffset.UtcNow,
                ServerInfoPath = manifest.ServerInfoPath,
                BackupPath = manifest.BackupPath,
                AppliedAuditBackupPath = appliedAuditBackupPath,
                TargetZoneExePath = manifest.TargetZoneExePath,
                TargetEntryName = manifest.TargetEntryName,
                WorldNo = manifest.WorldNo,
                ZoneNo = manifest.ZoneNo,
                OldMaxAccept = manifest.OldMaxAccept,
                NewMaxAccept = manifest.NewMaxAccept,
                OriginalSha256 = manifest.OriginalSha256,
                AppliedSha256 = manifest.AppliedSha256,
                CertifiedZoneSha256 = manifest.CertifiedZoneSha256
            };
            WriteJsonDurable(manifestTemp, rolledBack);
            File.Move(manifestTemp, manifestPath, overwrite: true);

            return new ZoneClientListenerConfigurationResult
            {
                Success = true,
                RolledBack = true,
                ServerInfoPath = serverInfoPath,
                BackupPath = backupPath,
                ManifestPath = manifestPath,
                AppliedAuditBackupPath = appliedAuditBackupPath,
                OriginalSha256 = restoredHash,
                AppliedSha256 = auditHash,
                Detail = $"LISTENER TEST CONFIG: ROLLED BACK. ServerInfo.txt ist wieder bytegenau im Originalzustand; die 2000er Testkonfiguration wurde als Auditbackup erhalten. Der Binary-Stand von {Path.GetFileName(Path.GetDirectoryName(targetZoneExe))} ist für diesen Rollback absichtlich irrelevant. Es wurde KEIN Zone-Prozess gestartet."
            };
        }
        catch (Exception ex)
        {
            SafeDelete(stagePath);
            SafeDelete(manifestTemp);
            return Failed("Listener-Rollback fehlgeschlagen: " + ex.Message, serverInfoPath: serverInfoPath, backupPath: backupPath, manifestPath: manifestPath);
        }
    }

    public ZoneClientListenerReadinessResult AnalyzeApplyReadiness(string targetZoneExePath)
    {
        if (!TryResolvePaths(targetZoneExePath, out var targetZoneExe, out var serverInfoPath, out var zoneNo, out var resolveError))
            return NotReady(resolveError);

        if (!File.Exists(targetZoneExe))
            return NotReady("Ziel-Zone.exe wurde nicht gefunden.", targetZoneExe, serverInfoPath, zoneNo);
        if (!File.Exists(serverInfoPath))
            return NotReady("ServerInfo.txt wurde relativ zum Ziel-Zone-Ordner nicht gefunden.", targetZoneExe, serverInfoPath, zoneNo);

        var targetHash = TrySha256File(targetZoneExe, out var targetHashError);
        if (targetHashError is not null)
            return NotReady("Ziel-Zone.exe konnte nicht gehasht werden: " + targetHashError, targetZoneExe, serverInfoPath, zoneNo);
        if (!targetHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
            return NotReady("Ziel-Zone.exe ist nicht exakt der zertifizierte 2000/12000/512 Patch-Build.", targetZoneExe, serverInfoPath, zoneNo);

        var deploymentError = ValidateDeploymentMetadata(targetZoneExe);
        if (deploymentError is not null)
            return NotReady(deploymentError, targetZoneExe, serverInfoPath, zoneNo);

        var processCheck = FindRunningZoneProcesses();
        if (!processCheck.ProcessEnumerationTrusted)
            return NotReady("Zone-Prozessstatus ist nicht zuverlässig prüfbar. Preflight arbeitet fail-closed.", targetZoneExe, serverInfoPath, zoneNo);
        if (processCheck.ProcessIds.Count > 0)
            return NotReady("Mindestens ein Zone-Prozess läuft: " + FormatPids(processCheck.ProcessIds), targetZoneExe, serverInfoPath, zoneNo);

        var backupPath = GetBackupPath(serverInfoPath);
        var manifestPath = GetManifestPath(serverInfoPath);
        var auditBackupPath = GetAppliedAuditBackupPath(serverInfoPath);
        if (File.Exists(backupPath) || File.Exists(manifestPath) || File.Exists(auditBackupPath))
            return NotReady("Es existieren bereits Listener-Test-Backup/Manifest/Auditdateien. Erst den vorhandenen Zustand sauber klären.", targetZoneExe, serverInfoPath, zoneNo);

        IReadOnlyList<ServerInfoEntry> entries;
        try
        {
            entries = _parser.Parse(serverInfoPath);
        }
        catch (Exception ex)
        {
            return NotReady("ServerInfo.txt konnte nicht geparst werden: " + ex.Message, targetZoneExe, serverInfoPath, zoneNo);
        }

        var candidates = entries
            .Where(x => x.ServerType == ZoneServerType
                        && x.ZoneNo == zoneNo
                        && x.ConnectionKind == ZoneClientConnectionKind)
            .ToList();
        if (candidates.Count != 1)
            return NotReady($"Die Ziel-Zone konnte in ServerInfo.txt nicht eindeutig auf genau eine Client-Listener-Zeile abgebildet werden (Treffer: {candidates.Count}).", targetZoneExe, serverInfoPath, zoneNo);

        var targetEntry = candidates[0];
        if (targetEntry.MaxAccept != StockMaxAccept)
            return NotReady($"Die Zielzeile besitzt nMaxAccept={targetEntry.MaxAccept:N0}; für diesen zertifizierten Test wird exakt der Stockzustand {StockMaxAccept:N0} erwartet.", targetZoneExe, serverInfoPath, zoneNo, targetEntry);

        var originalHash = TrySha256File(serverInfoPath, out var serverInfoHashError);
        if (serverInfoHashError is not null)
            return NotReady("ServerInfo.txt konnte nicht gehasht werden: " + serverInfoHashError, targetZoneExe, serverInfoPath, zoneNo, targetEntry);

        return new ZoneClientListenerReadinessResult
        {
            Ready = true,
            TargetZoneExePath = targetZoneExe,
            ServerInfoPath = serverInfoPath,
            ZoneNo = zoneNo,
            TargetEntry = targetEntry,
            OriginalServerInfoSha256 = originalHash,
            Detail = $"READY: {targetEntry.Name} (World {targetEntry.WorldNo}, Zone {targetEntry.ZoneNo}) kann kontrolliert von {StockMaxAccept:N0} auf {CertifiedMaxAccept:N0} Client-Sessions gesetzt werden. Ziel-Binary und Deployment-Metadaten sind zertifiziert; alle Zone-Prozesse sind gestoppt."
        };
    }

    private static ListenerRewriteResult RewriteTarget(string input, ServerInfoEntry target, int newMaxAccept)
    {
        var changed = 0;
        var output = ServerInfoLineRx.Replace(input, match =>
        {
            if (!MatchesTarget(match, target))
                return match.Value;

            if (!int.TryParse(match.Groups["maxaccept"].Value, out var currentMax) || currentMax != StockMaxAccept)
                return match.Value;

            changed++;
            return match.Groups["prefix"].Value + newMaxAccept + match.Groups["suffix"].Value;
        });

        return changed == 1
            ? new ListenerRewriteResult(true, output, changed, "Genau eine Zielzeile wurde geändert.")
            : new ListenerRewriteResult(false, input, changed, $"Zielzeile konnte nicht eindeutig geändert werden (Änderungen: {changed}).");
    }

    private static bool MatchesTarget(Match match, ServerInfoEntry target)
        => string.Equals(match.Groups["name"].Value, target.Name, StringComparison.OrdinalIgnoreCase)
           && TryGroupInt(match, "type") == target.ServerType
           && TryGroupInt(match, "world") == target.WorldNo
           && TryGroupInt(match, "zone") == target.ZoneNo
           && TryGroupInt(match, "kind") == target.ConnectionKind
           && string.Equals(match.Groups["host"].Value, target.Host, StringComparison.OrdinalIgnoreCase)
           && TryGroupInt(match, "port") == target.Port
           && TryGroupInt(match, "backlog") == target.BackLog;

    private static int TryGroupInt(Match match, string name)
        => int.TryParse(match.Groups[name].Value, out var value) ? value : int.MinValue;

    private void ValidateOnlyExpectedEntryChanged(
        string originalPath,
        string candidatePath,
        ServerInfoEntry expectedTarget,
        int expectedNewMaxAccept)
    {
        var before = _parser.Parse(originalPath);
        var after = _parser.Parse(candidatePath);
        if (before.Count != after.Count)
            throw new InvalidDataException("ServerInfo-Validierung: Die Anzahl parsebarer SERVER_INFO-Einträge hat sich geändert.");

        var differences = new List<int>();
        for (var i = 0; i < before.Count; i++)
        {
            if (before[i] != after[i])
                differences.Add(i);
        }

        if (differences.Count != 1)
            throw new InvalidDataException($"ServerInfo-Validierung: Erwartet wurde genau ein geänderter Eintrag, gefunden wurden {differences.Count}.");

        var index = differences[0];
        if (before[index] != expectedTarget)
            throw new InvalidDataException("ServerInfo-Validierung: Der geänderte Eintrag ist nicht die erwartete Ziel-Zone.");

        var expectedAfter = expectedTarget with { MaxAccept = expectedNewMaxAccept };
        if (after[index] != expectedAfter)
            throw new InvalidDataException("ServerInfo-Validierung: Neben nMaxAccept wurden unerwartete semantische Felder geändert.");
    }

    private static string? ValidateDeploymentMetadata(string targetZoneExePath)
    {
        var deploymentPath = targetZoneExePath + ".nextgen-deployment.json";
        if (!File.Exists(deploymentPath))
            return "Deployment-Metadaten der zertifizierten Test-Zone fehlen.";

        ZonePoolSingleZoneDeploymentMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<ZonePoolSingleZoneDeploymentMetadata>(File.ReadAllBytes(deploymentPath));
        }
        catch (Exception ex)
        {
            return "Deployment-Metadaten konnten nicht gelesen werden: " + ex.Message;
        }

        if (metadata is null
            || !string.Equals(metadata.State, "DEPLOYED", StringComparison.Ordinal)
            || !PathEquals(metadata.TargetPath, targetZoneExePath)
            || metadata.VerifiedSiteCount != 83
            || metadata.PlayerCapacity != ZonePoolOfflineWriterSelfTest.PlayerTarget
            || metadata.MobCapacity != ZonePoolOfflineWriterSelfTest.MobTarget
            || metadata.NpcCapacity != ZonePoolOfflineWriterSelfTest.NpcTarget
            || !metadata.PatchedSha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase)
            || !metadata.BaselineSha256.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
        {
            return "Deployment-Metadaten entsprechen nicht dem zertifizierten 2000/12000/512 Ein-Zonen-Testzustand.";
        }

        if (string.IsNullOrWhiteSpace(metadata.BackupPath) || !File.Exists(metadata.BackupPath))
            return "Das registrierte Baseline-Backup der Test-Zone fehlt.";

        var backupHash = TrySha256File(metadata.BackupPath, out var backupHashError);
        if (backupHashError is not null)
            return "Das registrierte Baseline-Backup konnte nicht gehasht werden: " + backupHashError;
        if (!backupHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
            return "Das registrierte Baseline-Backup entspricht nicht dem gepinnten NA2016-SHA.";

        return null;
    }

    private static bool TryResolvePaths(
        string targetZoneExePath,
        out string targetZoneExe,
        out string serverInfoPath,
        out int zoneNo,
        out string error)
    {
        targetZoneExe = string.Empty;
        serverInfoPath = string.Empty;
        zoneNo = -1;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(targetZoneExePath))
        {
            error = "Ziel-Zone.exe-Pfad fehlt.";
            return false;
        }

        try
        {
            targetZoneExe = Path.GetFullPath(targetZoneExePath);
            var zoneDir = Directory.GetParent(targetZoneExe);
            if (zoneDir is null)
            {
                error = "Ziel-Zone.exe besitzt keinen auflösbaren Zone-Ordner.";
                return false;
            }

            var folderMatch = ZoneFolderRx.Match(zoneDir.Name);
            if (!folderMatch.Success || !int.TryParse(folderMatch.Groups["zone"].Value, out zoneNo))
            {
                error = $"Zielordner '{zoneDir.Name}' entspricht nicht dem erwarteten Schema ZoneNN.";
                return false;
            }

            var serverRoot = zoneDir.Parent;
            if (serverRoot is null)
            {
                error = "Server-Root konnte aus dem Zone-Ordner nicht abgeleitet werden.";
                return false;
            }

            serverInfoPath = Path.Combine(serverRoot.FullName, "9Data", "ServerInfo", "ServerInfo.txt");
            return true;
        }
        catch (Exception ex)
        {
            error = "Pfadauflösung fehlgeschlagen: " + ex.Message;
            return false;
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

    private static EmergencyRollbackResult TryEmergencyRollback(string serverInfoPath, string backupPath, string expectedOriginalSha256)
    {
        if (!File.Exists(serverInfoPath) || !File.Exists(backupPath))
            return new EmergencyRollbackResult(false, "NOTFALL-ROLLBACK NICHT MÖGLICH: ServerInfo oder Backup fehlt.");

        try
        {
            var processCheck = FindRunningZoneProcesses();
            if (!processCheck.ProcessEnumerationTrusted || processCheck.ProcessIds.Count > 0)
                return new EmergencyRollbackResult(false, "NOTFALL-ROLLBACK BLOCKIERT: Zone-Prozessstatus ist nicht sicher oder eine Zone läuft.");

            if (!Sha256File(backupPath).Equals(expectedOriginalSha256, StringComparison.OrdinalIgnoreCase))
                return new EmergencyRollbackResult(false, "NOTFALL-ROLLBACK BLOCKIERT: Listener-Backup-Hash stimmt nicht.");

            var stage = serverInfoPath + ".nextgen-zone-listener-emergency-" + Guid.NewGuid().ToString("N");
            try
            {
                WriteDurable(stage, File.ReadAllBytes(backupPath));
                if (!Sha256File(stage).Equals(expectedOriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Notfall-Staging bestand die Hashprüfung nicht.");
                File.Replace(stage, serverInfoPath, null, ignoreMetadataErrors: false);
                if (!Sha256File(serverInfoPath).Equals(expectedOriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Notfall-Rollback bestand die finale Hashprüfung nicht.");
                return new EmergencyRollbackResult(true, "NOTFALL-ROLLBACK ERFOLGREICH: ServerInfo ist wieder im Originalzustand.");
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

    private static string GetBackupPath(string serverInfoPath)
        => serverInfoPath + ".nextgen-zone-listener.bak";

    private static string GetManifestPath(string serverInfoPath)
        => serverInfoPath + ".nextgen-zone-listener.json";

    private static string GetAppliedAuditBackupPath(string serverInfoPath)
        => serverInfoPath + ".nextgen-zone-listener-applied.bak";

    private static string FormatPids(IEnumerable<int> ids)
        => string.Join(", ", ids.Select(x => "PID " + x));

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

    private static string Sha256Bytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteDurable(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.WriteThrough);
        stream.Write(content, 0, content.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteJsonDurable(string path, ZoneClientListenerConfigurationMetadata metadata)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true });
        WriteDurable(path, json);
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static ZoneClientListenerReadinessResult NotReady(
        string detail,
        string targetZoneExePath = "",
        string serverInfoPath = "",
        int zoneNo = -1,
        ServerInfoEntry? targetEntry = null)
        => new()
        {
            Ready = false,
            TargetZoneExePath = targetZoneExePath,
            ServerInfoPath = serverInfoPath,
            ZoneNo = zoneNo,
            TargetEntry = targetEntry,
            Detail = detail
        };

    private static ZoneClientListenerConfigurationResult Failed(
        string detail,
        ZoneClientListenerReadinessResult? readiness = null,
        bool emergencyRollbackAttempted = false,
        bool emergencyRollbackSucceeded = false,
        string serverInfoPath = "",
        string backupPath = "",
        string manifestPath = "")
        => new()
        {
            Success = false,
            Readiness = readiness,
            EmergencyRollbackAttempted = emergencyRollbackAttempted,
            EmergencyRollbackSucceeded = emergencyRollbackSucceeded,
            ServerInfoPath = serverInfoPath,
            BackupPath = backupPath,
            ManifestPath = manifestPath,
            Detail = detail
        };

    private sealed record ListenerRewriteResult(bool Success, string Text, int ChangedCount, string Detail);
    private sealed record ProcessInventory(bool ProcessEnumerationTrusted, IReadOnlyList<int> ProcessIds);
    private sealed record EmergencyRollbackResult(bool Success, string Detail);
}

public sealed class ZoneClientListenerConfigurationMetadata
{
    public int FormatVersion { get; init; }
    public string State { get; init; } = string.Empty;
    public DateTimeOffset AppliedUtc { get; init; }
    public DateTimeOffset? RolledBackUtc { get; init; }
    public string ServerInfoPath { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string AppliedAuditBackupPath { get; init; } = string.Empty;
    public string TargetZoneExePath { get; init; } = string.Empty;
    public string TargetEntryName { get; init; } = string.Empty;
    public int WorldNo { get; init; }
    public int ZoneNo { get; init; }
    public int OldMaxAccept { get; init; }
    public int NewMaxAccept { get; init; }
    public string OriginalSha256 { get; init; } = string.Empty;
    public string AppliedSha256 { get; init; } = string.Empty;
    public string CertifiedZoneSha256 { get; init; } = string.Empty;
}

public sealed class ZoneClientListenerReadinessResult
{
    public bool Ready { get; init; }
    public string TargetZoneExePath { get; init; } = string.Empty;
    public string ServerInfoPath { get; init; } = string.Empty;
    public int ZoneNo { get; init; }
    public ServerInfoEntry? TargetEntry { get; init; }
    public string OriginalServerInfoSha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed class ZoneClientListenerConfigurationResult
{
    public bool Success { get; init; }
    public bool Applied { get; init; }
    public bool RolledBack { get; init; }
    public bool EmergencyRollbackAttempted { get; init; }
    public bool EmergencyRollbackSucceeded { get; init; }
    public ZoneClientListenerReadinessResult? Readiness { get; init; }
    public string ServerInfoPath { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string AppliedAuditBackupPath { get; init; } = string.Empty;
    public string OriginalSha256 { get; init; } = string.Empty;
    public string AppliedSha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
