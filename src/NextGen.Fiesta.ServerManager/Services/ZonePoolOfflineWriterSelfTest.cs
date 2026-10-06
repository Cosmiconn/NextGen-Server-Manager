using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Explicit diagnostic self-test for the guarded offline writer.
/// It targets only the verified 2000/12000/512 profile, operates in a fresh temporary
/// directory, independently verifies the generated copy, then rolls it back to stock.
/// </summary>
public sealed class ZonePoolOfflineWriterSelfTest
{
    public const int PlayerTarget = 2000;
    public const int MobTarget = 12000;
    public const int NpcTarget = 512;

    public const string ExpectedBaselineSha256 = "DB1CB42912556A4EA5CDE5C18F15F2495B81465C70CA9C18AD5BC7E36611AFF5";
    public const string ExpectedPatchedSha256 = "B8A6688A5648FB39363D7A39B64794DD42095F51D0A4205E40FC332147783EAC";

    private readonly ZonePoolOfflinePatchWriter _writer = new();
    private readonly ZonePoolPatchedCopyVerifier _verifier = new();

    public ZonePoolOfflineWriterSelfTestResult Run(string baselineZoneExePath)
    {
        if (string.IsNullOrWhiteSpace(baselineZoneExePath) || !File.Exists(baselineZoneExePath))
            return Failed("Baseline-Zone.exe wurde nicht gefunden.");

        var source = Path.GetFullPath(baselineZoneExePath);
        var sourceHashBefore = Sha256File(source);
        if (!sourceHashBefore.Equals(ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
            return Failed($"Baseline-Hash stimmt nicht: {sourceHashBefore}");

        var testDirectory = Path.Combine(Path.GetTempPath(), "NextGen-ZonePoolWriterSelfTest-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(testDirectory, "Zone.patched.exe");
        Directory.CreateDirectory(testDirectory);

        try
        {
            var create = _writer.CreatePatchedCopy(source, output, PlayerTarget, MobTarget, NpcTarget);
            if (!create.Success)
                return Failed("Patchkopie konnte nicht erzeugt werden: " + create.Detail, create, testDirectory);
            if (create.SafetyGate?.FullCoverageCertified != true
                || create.SafetyGate.OfflineWriterCertified != true
                || create.SafetyGate.CanCreateOfflinePatchedCopy != true
                || create.SafetyGate.CanWriteBinary != false)
                return Failed("Writer-Safety-Gate befindet sich nicht im erwarteten Offline-only-Zustand.", create, testDirectory);

            var verify = _verifier.Verify(output);
            if (!verify.Success)
                return Failed("Unabhängige Patchkopie-Verifikation fehlgeschlagen: " + verify.Detail, create, testDirectory, verification: verify);
            if (verify.VerifiedSiteCount != create.ChangedSiteCount)
                return Failed($"Verifier-Sitezahl {verify.VerifiedSiteCount} weicht vom Writer {create.ChangedSiteCount} ab.", create, testDirectory, verification: verify);

            var patchedHash = Sha256File(output);
            if (!patchedHash.Equals(ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase)
                || !patchedHash.Equals(create.PatchedSha256, StringComparison.OrdinalIgnoreCase)
                || !patchedHash.Equals(verify.PatchedSha256, StringComparison.OrdinalIgnoreCase))
                return Failed($"Patchkopie besitzt unerwarteten SHA-256: {patchedHash}", create, testDirectory, verification: verify);

            var restore = _writer.RestoreGeneratedCopyToBaseline(output);
            if (!restore.Success)
                return Failed("Rollback der erzeugten Kopie fehlgeschlagen: " + restore.Detail, create, testDirectory, restore, verify);

            var rolledBackHash = Sha256File(output);
            if (!rolledBackHash.Equals(ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                return Failed($"Rollback-Datei besitzt unerwarteten SHA-256: {rolledBackHash}", create, testDirectory, restore, verify);

            var sourceHashAfter = Sha256File(source);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                return Failed("Die originale Baseline-Datei wurde während des Selbsttests verändert.", create, testDirectory, restore, verify);

            return new ZonePoolOfflineWriterSelfTestResult
            {
                Success = true,
                CreateResult = create,
                VerificationResult = verify,
                RestoreResult = restore,
                BaselineSha256 = sourceHashAfter,
                PatchedSha256 = patchedHash,
                RollbackSha256 = rolledBackHash,
                TemporaryDirectory = testDirectory,
                Detail = $"SELFTEST OK: Offline-COPY zertifiziert und unabhängig rückverifiziert ({verify.VerifiedSiteCount} geänderte Sites), " +
                         $"Live/In-Place weiterhin gesperrt, Patch SHA {patchedHash}, Rollback SHA {rolledBackHash}, Original unverändert."
            };
        }
        catch (Exception ex)
        {
            return Failed("Selbsttest-Ausnahme: " + ex.Message, directory: testDirectory);
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, recursive: true);
            }
            catch { }
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ZonePoolOfflineWriterSelfTestResult Failed(
        string detail,
        ZonePoolOfflinePatchWriteResult? create = null,
        string directory = "",
        ZonePoolOfflinePatchWriteResult? restore = null,
        ZonePoolPatchedCopyVerificationResult? verification = null)
        => new()
        {
            Success = false,
            CreateResult = create,
            RestoreResult = restore,
            VerificationResult = verification,
            TemporaryDirectory = directory,
            Detail = detail
        };
}

public sealed class ZonePoolOfflineWriterSelfTestResult
{
    public bool Success { get; init; }
    public ZonePoolOfflinePatchWriteResult? CreateResult { get; init; }
    public ZonePoolPatchedCopyVerificationResult? VerificationResult { get; init; }
    public ZonePoolOfflinePatchWriteResult? RestoreResult { get; init; }
    public string BaselineSha256 { get; init; } = string.Empty;
    public string PatchedSha256 { get; init; } = string.Empty;
    public string RollbackSha256 { get; init; } = string.Empty;
    public string TemporaryDirectory { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
