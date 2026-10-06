using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Explicit diagnostic self-test for the guarded offline writer.
/// It targets only the verified 2000/12000/512 profile, operates in a fresh temporary
/// directory, validates the independently established target SHA-256, rolls the generated
/// copy back to the stock SHA-256 and removes the temporary artifacts afterwards.
/// </summary>
public sealed class ZonePoolOfflineWriterSelfTest
{
    public const int PlayerTarget = 2000;
    public const int MobTarget = 12000;
    public const int NpcTarget = 512;

    public const string ExpectedBaselineSha256 = "DB1CB42912556A4EA5CDE5C18F15F2495B81465C70CA9C18AD5BC7E36611AFF5";
    public const string ExpectedPatchedSha256 = "B8A6688A5648FB39363D7A39B64794DD42095F51D0A4205E40FC332147783EAC";

    private readonly ZonePoolOfflinePatchWriter _writer = new();

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
            var create = _writer.CreatePatchedCopy(
                source,
                output,
                PlayerTarget,
                MobTarget,
                NpcTarget);
            if (!create.Success)
                return Failed("Patchkopie konnte nicht erzeugt werden: " + create.Detail, create, testDirectory);
            if (create.SafetyGate?.FullCoverageCertified != true)
                return Failed("Writer meldete Erfolg ohne FullCoverageCertified.", create, testDirectory);
            if (create.SafetyGate?.OfflineWriterCertified != true)
                return Failed("Writer meldete Erfolg ohne OfflineWriterCertified.", create, testDirectory);
            if (create.SafetyGate?.CanCreateOfflinePatchedCopy != true)
                return Failed("Writer meldete Erfolg ohne CanCreateOfflinePatchedCopy.", create, testDirectory);
            if (create.SafetyGate?.CanWriteBinary != false)
                return Failed("Safety-Gate hat Live-/In-Place-Binärschreiben unerwartet freigegeben.", create, testDirectory);
            if (!File.Exists(output))
                return Failed("Writer meldete Erfolg, aber die Patchkopie fehlt.", create, testDirectory);

            var patchedHash = Sha256File(output);
            if (!patchedHash.Equals(ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
                return Failed($"Patchkopie besitzt unerwarteten SHA-256: {patchedHash}", create, testDirectory);
            if (!patchedHash.Equals(create.PatchedSha256, StringComparison.OrdinalIgnoreCase))
                return Failed("Writer-Rückgabewert und tatsächlicher Patch-SHA unterscheiden sich.", create, testDirectory);

            var restore = _writer.RestoreGeneratedCopyToBaseline(output);
            if (!restore.Success)
                return Failed("Rollback der erzeugten Kopie fehlgeschlagen: " + restore.Detail, create, testDirectory, restore);

            var rolledBackHash = Sha256File(output);
            if (!rolledBackHash.Equals(ExpectedBaselineSha256, StringComparison.OrdinalIgnoreCase))
                return Failed($"Rollback-Datei besitzt unerwarteten SHA-256: {rolledBackHash}", create, testDirectory, restore);

            var sourceHashAfter = Sha256File(source);
            if (!sourceHashAfter.Equals(sourceHashBefore, StringComparison.OrdinalIgnoreCase))
                return Failed("Die originale Baseline-Datei wurde während des Selbsttests verändert.", create, testDirectory, restore);

            return new ZonePoolOfflineWriterSelfTestResult
            {
                Success = true,
                CreateResult = create,
                RestoreResult = restore,
                BaselineSha256 = sourceHashAfter,
                PatchedSha256 = patchedHash,
                RollbackSha256 = rolledBackHash,
                TemporaryDirectory = testDirectory,
                Detail = $"SELFTEST OK: Offline-COPY zertifiziert, Live/In-Place weiterhin gesperrt, Patch SHA {patchedHash}, Rollback SHA {rolledBackHash}, Original unverändert. Testartefakte werden entfernt."
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
            catch
            {
                // Cleanup failure must not alter patch/rollback verdict; caller receives
                // the temporary directory path and can remove it manually if required.
            }
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
        ZonePoolOfflinePatchWriteResult? restore = null)
        => new()
        {
            Success = false,
            CreateResult = create,
            RestoreResult = restore,
            TemporaryDirectory = directory,
            Detail = detail
        };
}

public sealed class ZonePoolOfflineWriterSelfTestResult
{
    public bool Success { get; init; }
    public ZonePoolOfflinePatchWriteResult? CreateResult { get; init; }
    public ZonePoolOfflinePatchWriteResult? RestoreResult { get; init; }
    public string BaselineSha256 { get; init; } = string.Empty;
    public string PatchedSha256 { get; init; } = string.Empty;
    public string RollbackSha256 { get; init; } = string.Empty;
    public string TemporaryDirectory { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
