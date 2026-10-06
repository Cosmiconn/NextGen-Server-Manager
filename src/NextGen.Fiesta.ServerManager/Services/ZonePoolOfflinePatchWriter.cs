using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Guarded OFFLINE writer for a patched Zone.exe COPY.
/// It never modifies the supplied baseline Zone.exe in place and refuses to operate
/// while any Zone process is running. The source must pass the complete rebase safety
/// gate. Output is staged to a temp file, hash-verified, then atomically renamed.
/// </summary>
public sealed class ZonePoolOfflinePatchWriter
{
    private readonly ZonePoolRebaseSafetyGate _safetyGate = new();
    private readonly ZonePoolPatchManifest _manifestBuilder = new();

    public ZonePoolOfflinePatchWriteResult CreatePatchedCopy(
        string baselineZoneExePath,
        string outputZoneExePath,
        int playerCapacity,
        int mobCapacity,
        int npcCapacity)
    {
        if (string.IsNullOrWhiteSpace(baselineZoneExePath) || !File.Exists(baselineZoneExePath))
            return Failed("Baseline-Zone.exe wurde nicht gefunden.");
        if (string.IsNullOrWhiteSpace(outputZoneExePath))
            return Failed("Ausgabepfad fehlt.");

        var sourceFull = Path.GetFullPath(baselineZoneExePath);
        var outputFull = Path.GetFullPath(outputZoneExePath);
        if (string.Equals(sourceFull, outputFull, StringComparison.OrdinalIgnoreCase))
            return Failed("In-Place-Patching ist verboten. Ausgabe muss eine neue Datei sein.");

        var running = FindRunningZoneProcesses();
        if (running.Count > 0)
            return Failed("BLOCKIERT: Mindestens ein Zone-Prozess läuft: " + string.Join(", ", running.Select(x => $"PID {x.ProcessId}")));

        if (File.Exists(outputFull))
            return Failed("Ausgabedatei existiert bereits; automatisches Überschreiben ist verboten: " + outputFull);

        var gate = _safetyGate.Evaluate(sourceFull, playerCapacity, mobCapacity, npcCapacity);
        if (!gate.FullCoverageCertified
            || !gate.OfflineWriterCertified
            || !gate.CanCreateOfflinePatchedCopy
            || gate.CanWriteBinary)
        {
            return Failed("Offline-COPY-Writer ist für dieses Profil nicht vollständig zertifiziert: " + gate.Detail, gate);
        }

        var manifest = gate.PatchManifest ?? _manifestBuilder.Build(sourceFull, playerCapacity, mobCapacity, npcCapacity);
        if (!manifest.ManifestVerified || !manifest.NoOverlaps || !manifest.RollbackVerified)
            return Failed("Offline-Patchmanifest ist nicht vollständig verifiziert: " + manifest.Detail, gate, manifest);

        byte[] baseline;
        try { baseline = File.ReadAllBytes(sourceFull); }
        catch (Exception ex) { return Failed("Baseline konnte nicht gelesen werden: " + ex.Message, gate, manifest); }

        var sourceHash = Sha256(baseline);
        if (!sourceHash.Equals(manifest.BaselineSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Baseline-Hash änderte sich nach Safety-Gate. Abbruch ohne Schreibzugriff.", gate, manifest);

        var patched = baseline.ToArray();
        foreach (var site in manifest.ChangedSites.OrderBy(x => x.FileOffset))
        {
            if (site.FileOffset < 0 || site.FileOffset + site.ExpectedBytes.Length > patched.Length)
                return Failed("Patchsite außerhalb der Datei: " + site.Name, gate, manifest);
            if (!patched.AsSpan(site.FileOffset, site.ExpectedBytes.Length).SequenceEqual(site.ExpectedBytes))
                return Failed("Patch-Precondition stimmt nicht mehr: " + site.Name, gate, manifest);
            site.ReplacementBytes.CopyTo(patched, site.FileOffset);
        }

        var patchedHash = Sha256(patched);
        if (!patchedHash.Equals(manifest.ProspectivePatchedSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Simulierter Zielhash und Writer-Zielhash unterscheiden sich. Kein Output veröffentlicht.", gate, manifest);

        var outputDir = Path.GetDirectoryName(outputFull);
        if (string.IsNullOrWhiteSpace(outputDir))
            return Failed("Ausgabeverzeichnis konnte nicht bestimmt werden.", gate, manifest);
        Directory.CreateDirectory(outputDir);

        var backupFull = outputFull + ".baseline.bak";
        var metadataFull = outputFull + ".nextgen-patch.json";
        var tempFull = outputFull + ".tmp-" + Guid.NewGuid().ToString("N");
        var backupTemp = backupFull + ".tmp-" + Guid.NewGuid().ToString("N");
        var metadataTemp = metadataFull + ".tmp-" + Guid.NewGuid().ToString("N");

        if (File.Exists(backupFull) || File.Exists(metadataFull))
            return Failed("Backup oder Patch-Metadaten existieren bereits; automatisches Überschreiben ist verboten.", gate, manifest);

        try
        {
            WriteDurable(tempFull, patched);
            if (!Sha256File(tempFull).Equals(patchedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Temp-Patchdatei bestand die Hash-Verifikation nicht.");

            WriteDurable(backupTemp, baseline);
            if (!Sha256File(backupTemp).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Baseline-Backup bestand die Hash-Verifikation nicht.");

            var metadata = new ZonePoolOfflinePatchMetadata
            {
                FormatVersion = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
                SourcePath = sourceFull,
                OutputPath = outputFull,
                BaselineSha256 = sourceHash,
                PatchedSha256 = patchedHash,
                PlayerCapacity = playerCapacity,
                MobCapacity = mobCapacity,
                NpcCapacity = npcCapacity,
                ChangedSiteCount = manifest.ChangedSites.Count,
                ChangedSites = manifest.ChangedSites.Select(x => new ZonePoolOfflinePatchMetadataSite
                {
                    Name = x.Name,
                    Category = x.Category,
                    VirtualAddress = $"0x{x.VirtualAddress:X8}",
                    FileOffset = x.FileOffset,
                    ExpectedHex = x.ExpectedHex,
                    ReplacementHex = x.ReplacementHex
                }).ToArray()
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true });
            WriteDurable(metadataTemp, json);

            // Publish only after all three staged artifacts are validated.
            File.Move(backupTemp, backupFull);
            File.Move(metadataTemp, metadataFull);
            File.Move(tempFull, outputFull);

            // Final post-publish verification. If the output somehow changed, keep the
            // verified baseline backup and metadata but never report success.
            if (!Sha256File(outputFull).Equals(patchedHash, StringComparison.OrdinalIgnoreCase))
                return Failed("Veröffentlichte Patchkopie bestand die finale Hashprüfung nicht.", gate, manifest);
            if (!Sha256File(backupFull).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                return Failed("Veröffentlichtes Baseline-Backup bestand die finale Hashprüfung nicht.", gate, manifest);

            return new ZonePoolOfflinePatchWriteResult
            {
                Success = true,
                SafetyGate = gate,
                Manifest = manifest,
                BaselinePath = sourceFull,
                PatchedPath = outputFull,
                BackupPath = backupFull,
                MetadataPath = metadataFull,
                BaselineSha256 = sourceHash,
                PatchedSha256 = patchedHash,
                ChangedSiteCount = manifest.ChangedSites.Count,
                Detail = $"Offline-Patchkopie erstellt und hashverifiziert. Original unverändert. {manifest.ChangedSites.Count} Sites geändert; Backup und Manifest liegen neben der Ausgabe."
            };
        }
        catch (Exception ex)
        {
            SafeDelete(tempFull);
            SafeDelete(backupTemp);
            SafeDelete(metadataTemp);
            return Failed("Offline-Writer abgebrochen: " + ex.Message, gate, manifest);
        }
    }

    /// <summary>
    /// Restores a previously generated patched COPY from its sidecar baseline backup.
    /// The original source file is not involved. Destination replacement is staged.
    /// </summary>
    public ZonePoolOfflinePatchWriteResult RestoreGeneratedCopyToBaseline(string patchedZoneExePath)
    {
        if (string.IsNullOrWhiteSpace(patchedZoneExePath))
            return Failed("Patchkopie-Pfad fehlt.");

        var patchedFull = Path.GetFullPath(patchedZoneExePath);
        var backupFull = patchedFull + ".baseline.bak";
        var metadataFull = patchedFull + ".nextgen-patch.json";
        if (!File.Exists(patchedFull) || !File.Exists(backupFull) || !File.Exists(metadataFull))
            return Failed("Patchkopie, Baseline-Backup oder Metadaten fehlen.");

        var running = FindRunningZoneProcesses();
        if (running.Count > 0)
            return Failed("BLOCKIERT: Mindestens ein Zone-Prozess läuft.");

        ZonePoolOfflinePatchMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<ZonePoolOfflinePatchMetadata>(File.ReadAllBytes(metadataFull));
        }
        catch (Exception ex)
        {
            return Failed("Patch-Metadaten konnten nicht gelesen werden: " + ex.Message);
        }
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.BaselineSha256) || string.IsNullOrWhiteSpace(metadata.PatchedSha256))
            return Failed("Patch-Metadaten sind unvollständig.");

        var currentHash = Sha256File(patchedFull);
        if (!currentHash.Equals(metadata.PatchedSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Aktuelle Patchkopie entspricht nicht dem registrierten Patch-Hash; automatischer Rollback verweigert.");
        var backupHash = Sha256File(backupFull);
        if (!backupHash.Equals(metadata.BaselineSha256, StringComparison.OrdinalIgnoreCase)
            || !backupHash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Baseline-Backup entspricht nicht dem verifizierten NA2016-Baseline-Hash.");

        var temp = patchedFull + ".rollback-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteDurable(temp, File.ReadAllBytes(backupFull));
            if (!Sha256File(temp).Equals(metadata.BaselineSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Rollback-Tempdatei bestand die Hashprüfung nicht.");
            File.Move(temp, patchedFull, overwrite: true);
            var finalHash = Sha256File(patchedFull);
            if (!finalHash.Equals(metadata.BaselineSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Rollback-Zieldatei bestand die finale Hashprüfung nicht.");

            return new ZonePoolOfflinePatchWriteResult
            {
                Success = true,
                PatchedPath = patchedFull,
                BackupPath = backupFull,
                MetadataPath = metadataFull,
                BaselineSha256 = metadata.BaselineSha256,
                PatchedSha256 = metadata.PatchedSha256,
                ChangedSiteCount = metadata.ChangedSiteCount,
                Detail = "Generierte Patchkopie wurde bytegenau auf die verifizierte NA2016-Baseline zurückgesetzt."
            };
        }
        catch (Exception ex)
        {
            SafeDelete(temp);
            return Failed("Rollback abgebrochen: " + ex.Message);
        }
    }

    private static IReadOnlyList<RunningZoneProcess> FindRunningZoneProcesses()
    {
        var result = new List<RunningZoneProcess>();
        try
        {
            foreach (var process in Process.GetProcessesByName("Zone"))
            {
                try { result.Add(new RunningZoneProcess(process.Id, process.ProcessName)); }
                finally { process.Dispose(); }
            }
        }
        catch
        {
            // Fail closed if process enumeration itself cannot be trusted.
            result.Add(new RunningZoneProcess(-1, "Zone-Prozessstatus nicht prüfbar"));
        }
        return result;
    }

    private static void WriteDurable(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.WriteThrough);
        stream.Write(content, 0, content.Length);
        stream.Flush(flushToDisk: true);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static ZonePoolOfflinePatchWriteResult Failed(
        string detail,
        ZonePoolRebaseSafetyGateResult? gate = null,
        ZonePoolPatchManifestResult? manifest = null)
        => new()
        {
            Success = false,
            SafetyGate = gate,
            Manifest = manifest,
            Detail = detail
        };

    private readonly record struct RunningZoneProcess(int ProcessId, string Name);
}

public sealed class ZonePoolOfflinePatchWriteResult
{
    public bool Success { get; init; }
    public ZonePoolRebaseSafetyGateResult? SafetyGate { get; init; }
    public ZonePoolPatchManifestResult? Manifest { get; init; }
    public string BaselinePath { get; init; } = string.Empty;
    public string PatchedPath { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string MetadataPath { get; init; } = string.Empty;
    public string BaselineSha256 { get; init; } = string.Empty;
    public string PatchedSha256 { get; init; } = string.Empty;
    public int ChangedSiteCount { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed class ZonePoolOfflinePatchMetadata
{
    public int FormatVersion { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public string BaselineSha256 { get; init; } = string.Empty;
    public string PatchedSha256 { get; init; } = string.Empty;
    public int PlayerCapacity { get; init; }
    public int MobCapacity { get; init; }
    public int NpcCapacity { get; init; }
    public int ChangedSiteCount { get; init; }
    public IReadOnlyList<ZonePoolOfflinePatchMetadataSite> ChangedSites { get; init; } = Array.Empty<ZonePoolOfflinePatchMetadataSite>();
}

public sealed class ZonePoolOfflinePatchMetadataSite
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string VirtualAddress { get; init; } = string.Empty;
    public int FileOffset { get; init; }
    public string ExpectedHex { get; init; } = string.Empty;
    public string ReplacementHex { get; init; } = string.Empty;
}
