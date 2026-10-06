using System.Security.Cryptography;
using System.Text.Json;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only verifier for a Zone.exe copy produced by ZonePoolOfflinePatchWriter.
/// Verification is intentionally independent from the write operation: it rebuilds the
/// certified manifest from the sidecar baseline backup, compares every changed site and
/// proves that reversing those sites in memory yields the exact baseline bytes/hash.
/// No file is modified.
/// </summary>
public sealed class ZonePoolPatchedCopyVerifier
{
    private readonly ZonePoolRebaseSafetyGate _safetyGate = new();

    public ZonePoolPatchedCopyVerificationResult Verify(string patchedZoneExePath)
    {
        if (string.IsNullOrWhiteSpace(patchedZoneExePath))
            return Failed("Patchkopie-Pfad fehlt.");

        var patchedPath = Path.GetFullPath(patchedZoneExePath);
        var backupPath = patchedPath + ".baseline.bak";
        var metadataPath = patchedPath + ".nextgen-patch.json";
        if (!File.Exists(patchedPath) || !File.Exists(backupPath) || !File.Exists(metadataPath))
            return Failed("Patchkopie, Baseline-Backup oder Patch-Metadaten fehlen.");

        byte[] patched;
        byte[] baseline;
        ZonePoolOfflinePatchMetadata? metadata;
        try
        {
            patched = File.ReadAllBytes(patchedPath);
            baseline = File.ReadAllBytes(backupPath);
            metadata = JsonSerializer.Deserialize<ZonePoolOfflinePatchMetadata>(File.ReadAllBytes(metadataPath));
        }
        catch (Exception ex)
        {
            return Failed("Patchartefakte konnten nicht gelesen werden: " + ex.Message);
        }

        if (metadata is null)
            return Failed("Patch-Metadaten konnten nicht deserialisiert werden.");

        var baselineHash = Sha256(baseline);
        var patchedHash = Sha256(patched);
        if (!baselineHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Baseline-Backup besitzt nicht den verifizierten NA2016-Hash.", baselineHash, patchedHash);
        if (!patchedHash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Patchkopie besitzt nicht den gepinnten 2000/12000/512-Zielhash.", baselineHash, patchedHash);

        if (!metadata.BaselineSha256.Equals(baselineHash, StringComparison.OrdinalIgnoreCase)
            || !metadata.PatchedSha256.Equals(patchedHash, StringComparison.OrdinalIgnoreCase))
            return Failed("Sidecar-Hashes stimmen nicht mit den tatsächlichen Dateien überein.", baselineHash, patchedHash);

        if (metadata.PlayerCapacity != ZonePoolOfflineWriterSelfTest.PlayerTarget
            || metadata.MobCapacity != ZonePoolOfflineWriterSelfTest.MobTarget
            || metadata.NpcCapacity != ZonePoolOfflineWriterSelfTest.NpcTarget)
            return Failed("Sidecar-Profil ist nicht das zertifizierte 2000/12000/512-Profil.", baselineHash, patchedHash);

        var gate = _safetyGate.Evaluate(
            backupPath,
            ZonePoolOfflineWriterSelfTest.PlayerTarget,
            ZonePoolOfflineWriterSelfTest.MobTarget,
            ZonePoolOfflineWriterSelfTest.NpcTarget);
        if (!gate.FullCoverageCertified || !gate.OfflineWriterCertified || !gate.CanCreateOfflinePatchedCopy || gate.CanWriteBinary)
            return Failed("Safety-Gate für das Baseline-Backup ist nicht im erwarteten Offline-only-Zustand: " + gate.Detail,
                baselineHash, patchedHash, gate);

        var manifest = gate.PatchManifest;
        if (manifest is null || !manifest.ManifestVerified || !manifest.NoOverlaps || !manifest.RollbackVerified)
            return Failed("Verifiziertes Patchmanifest konnte aus dem Baseline-Backup nicht rekonstruiert werden.",
                baselineHash, patchedHash, gate);

        var expectedChanged = manifest.ChangedSites.OrderBy(x => x.FileOffset).ToArray();
        var metadataSites = metadata.ChangedSites.OrderBy(x => x.FileOffset).ToArray();
        if (metadata.ChangedSiteCount != expectedChanged.Length || metadataSites.Length != expectedChanged.Length)
            return Failed($"Sidecar-Sitezahl stimmt nicht: {metadataSites.Length}/{metadata.ChangedSiteCount} statt {expectedChanged.Length}.",
                baselineHash, patchedHash, gate, manifest);

        for (var i = 0; i < expectedChanged.Length; i++)
        {
            var expected = expectedChanged[i];
            var recorded = metadataSites[i];
            if (!string.Equals(recorded.Name, expected.Name, StringComparison.Ordinal)
                || !string.Equals(recorded.Category, expected.Category, StringComparison.Ordinal)
                || recorded.FileOffset != expected.FileOffset
                || !string.Equals(recorded.VirtualAddress, $"0x{expected.VirtualAddress:X8}", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(recorded.ExpectedHex, expected.ExpectedHex, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(recorded.ReplacementHex, expected.ReplacementHex, StringComparison.OrdinalIgnoreCase))
            {
                return Failed("Sidecar-Site weicht vom rekonstruierten Manifest ab: " + expected.Name,
                    baselineHash, patchedHash, gate, manifest);
            }

            if (expected.FileOffset < 0 || expected.FileOffset + expected.ReplacementBytes.Length > patched.Length)
                return Failed("Patchsite liegt außerhalb der Patchkopie: " + expected.Name,
                    baselineHash, patchedHash, gate, manifest);
            if (!patched.AsSpan(expected.FileOffset, expected.ReplacementBytes.Length).SequenceEqual(expected.ReplacementBytes))
                return Failed("Replacement-Bytes fehlen/abweichend: " + expected.Name,
                    baselineHash, patchedHash, gate, manifest);
        }

        // This also proves there are no extra modifications outside the manifest: if any
        // unrelated byte differs, reversing only the certified sites cannot equal baseline.
        var rollback = patched.ToArray();
        foreach (var site in expectedChanged.Reverse())
        {
            if (!rollback.AsSpan(site.FileOffset, site.ReplacementBytes.Length).SequenceEqual(site.ReplacementBytes))
                return Failed("Rollback-Precondition fehlgeschlagen: " + site.Name,
                    baselineHash, patchedHash, gate, manifest);
            site.ExpectedBytes.CopyTo(rollback, site.FileOffset);
        }

        var rollbackHash = Sha256(rollback);
        if (!rollbackHash.Equals(baselineHash, StringComparison.OrdinalIgnoreCase)
            || !rollback.AsSpan().SequenceEqual(baseline))
            return Failed("In-Memory-Rollback ergibt nicht bytegenau die Baseline; zusätzliche Änderung erkannt.",
                baselineHash, patchedHash, gate, manifest, rollbackHash);

        return new ZonePoolPatchedCopyVerificationResult
        {
            Success = true,
            BaselineSha256 = baselineHash,
            PatchedSha256 = patchedHash,
            RollbackSha256 = rollbackHash,
            VerifiedSiteCount = expectedChanged.Length,
            SafetyGate = gate,
            Manifest = manifest,
            Detail = $"VERIFIED: Patchkopie entspricht exakt dem zertifizierten 2000/12000/512-Profil. " +
                     $"{expectedChanged.Length} geänderte Sites stimmen; keine Zusatzänderungen; In-Memory-Rollback ergibt exakt {baselineHash}."
        };
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static ZonePoolPatchedCopyVerificationResult Failed(
        string detail,
        string baselineHash = "",
        string patchedHash = "",
        ZonePoolRebaseSafetyGateResult? gate = null,
        ZonePoolPatchManifestResult? manifest = null,
        string rollbackHash = "")
        => new()
        {
            Success = false,
            BaselineSha256 = baselineHash,
            PatchedSha256 = patchedHash,
            RollbackSha256 = rollbackHash,
            SafetyGate = gate,
            Manifest = manifest,
            Detail = detail
        };
}

public sealed class ZonePoolPatchedCopyVerificationResult
{
    public bool Success { get; init; }
    public string BaselineSha256 { get; init; } = string.Empty;
    public string PatchedSha256 { get; init; } = string.Empty;
    public string RollbackSha256 { get; init; } = string.Empty;
    public int VerifiedSiteCount { get; init; }
    public ZonePoolRebaseSafetyGateResult? SafetyGate { get; init; }
    public ZonePoolPatchManifestResult? Manifest { get; init; }
    public string Detail { get; init; } = string.Empty;
}
