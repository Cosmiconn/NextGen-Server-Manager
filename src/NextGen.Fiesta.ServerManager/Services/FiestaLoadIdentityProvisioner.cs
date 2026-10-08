using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Separates account/character provisioning from the actual ShinePlayer capacity benchmark.
/// Each identity is admitted only far enough to prove an authoritative SH3/20 character record
/// and a successful SH4/3 ZoneRedirect, then the World connection is closed again.
/// </summary>
public sealed class FiestaLoadIdentityProvisioner
{
    public async Task<FiestaLoadIdentityProvisionResult> ProvisionAsync(
        FiestaLoadIdentityProvisionOptions options,
        Action<FiestaLoadIdentityProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var manifest = FiestaLoadCredentialManifest.Load(options.CredentialManifestPath);
        if (!manifest.IsLoginAutoRegistrationCompatible(out var compatibility))
        {
            return new FiestaLoadIdentityProvisionResult
            {
                Success = false,
                Detail = "IDENTITY PROVISION BLOCKED · " + compatibility
            };
        }

        if (manifest.Clients.Any(x => x.CreateCharacterIfMissing)
            && string.IsNullOrWhiteSpace(options.ClientOptions.CharacterCreateTemplatePath))
        {
            return new FiestaLoadIdentityProvisionResult
            {
                Success = false,
                Detail =
                    "IDENTITY PROVISION BLOCKED · Mindestens ein Testaccount darf einen Charakter erzeugen, " +
                    "aber CharacterCreateTemplatePath fehlt."
            };
        }

        var total = manifest.Clients.Count;
        var completed = 0;
        var createdOrVerified = new List<FiestaLoadClientCredential>(total);
        var failures = new List<string>();

        progress?.Invoke(new FiestaLoadIdentityProvisionProgress(
            "START",
            completed,
            total,
            string.Empty,
            $"Vorprovisionierung startet · {total:N0} Identitäten · maximal {options.MaxConcurrency} parallel."));

        for (var offset = 0; offset < total; offset += options.MaxConcurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = manifest.Clients
                .Skip(offset)
                .Take(options.MaxConcurrency)
                .ToArray();

            var batchTasks = batch.Select(async credential =>
            {
                var client = new FiestaHeadlessLoadClient();
                var clientOptions = CloneClientOptions(options.ClientOptions);

                var result = await client.ProvisionIdentityAsync(
                    clientOptions,
                    credential,
                    cancellationToken,
                    p =>
                    {
                        if (p.Failed
                            || p.Stage is FiestaLoadClientStage.CharacterCreated
                                or FiestaLoadClientStage.ZoneRedirectReceived)
                        {
                            progress?.Invoke(new FiestaLoadIdentityProvisionProgress(
                                p.Failed ? "CLIENT-FAIL" : p.Stage.ToString().ToUpperInvariant(),
                                Volatile.Read(ref completed),
                                total,
                                credential.Username,
                                p.Detail));
                        }
                    });

                return (Credential: credential, Result: result);
            }).ToArray();

            var batchResults = await Task.WhenAll(batchTasks);

            foreach (var item in batchResults)
            {
                if (item.Result.Success)
                {
                    createdOrVerified.Add(CloneCredential(item.Credential, createCharacterIfMissing: false));
                    completed++;
                    progress?.Invoke(new FiestaLoadIdentityProvisionProgress(
                        "PROVISIONED",
                        completed,
                        total,
                        item.Credential.Username,
                        item.Result.Detail));
                }
                else
                {
                    failures.Add(
                        $"{item.Credential.Username}/{item.Credential.CharacterName}: {item.Result.Detail}");
                }
            }

            // Fail closed after the current low-concurrency batch. Re-running is safe:
            // already provisioned r_ identities simply verify through SH3/20/SH4/3 again.
            if (failures.Count > 0)
            {
                return new FiestaLoadIdentityProvisionResult
                {
                    Success = false,
                    ProvisionedCount = completed,
                    TotalCount = total,
                    FailureSamples = failures.Take(12).ToArray(),
                    Detail =
                        $"IDENTITY PROVISION BLOCKED · {completed:N0}/{total:N0} verifiziert · " +
                        $"{failures.Count:N0} Fehler in Batch ab Index {offset + 1:N0}. · " +
                        string.Join(" | ", failures.Take(6))
                };
            }

            if (offset + batch.Length < total && options.BatchPause > TimeSpan.Zero)
                await Task.Delay(options.BatchPause, cancellationToken);
        }

        if (createdOrVerified.Count != total)
        {
            return new FiestaLoadIdentityProvisionResult
            {
                Success = false,
                ProvisionedCount = createdOrVerified.Count,
                TotalCount = total,
                Detail =
                    $"IDENTITY PROVISION BLOCKED · intern nur {createdOrVerified.Count:N0}/{total:N0} verifizierte Identitäten."
            };
        }

        var strictManifest = new FiestaLoadCredentialManifest
        {
            Clients = createdOrVerified
        };
        strictManifest.Validate();

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outputPath = Path.Combine(
            outputDirectory,
            $"load-credentials-{total}-provisioned-{stamp}.json");

        var json = JsonSerializer.Serialize(
            strictManifest,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            outputPath,
            json + Environment.NewLine,
            new UTF8Encoding(false));

        var sha = Sha256File(outputPath);
        progress?.Invoke(new FiestaLoadIdentityProvisionProgress(
            "PASS",
            total,
            total,
            string.Empty,
            $"Alle {total:N0} Identitäten sind vorprovisioniert; Benchmark-Manifest {Path.GetFileName(outputPath)}."));

        return new FiestaLoadIdentityProvisionResult
        {
            Success = true,
            ProvisionedCount = total,
            TotalCount = total,
            ProvisionedManifestPath = outputPath,
            ManifestSha256 = sha,
            Detail =
                $"IDENTITY PROVISION PASS · {total:N0}/{total:N0} Accounts/Charaktere per Originalprotokoll verifiziert · " +
                $"SH3/20 + Charakterauswahl + SH4/3 für jede Identität bestätigt · " +
                $"Benchmark-Manifest deaktiviert Auto-Create: {Path.GetFileName(outputPath)} · SHA {sha[..12]}."
        };
    }

    public static FiestaLoadIdentityProvisionSelfTestResult RunSelfTest()
    {
        try
        {
            var source = new FiestaLoadClientCredential
            {
                Username = "r_ngt000001",
                PasswordMd5 = "21232f297a57a5a743894a0e4a801fc3",
                CharacterName = "NGT000001",
                Slot = 0,
                CreateCharacterIfMissing = true
            };
            var strict = CloneCredential(source, createCharacterIfMissing: false);
            strict.Validate();

            if (strict.CreateCharacterIfMissing
                || strict.Username != source.Username
                || strict.CharacterName != source.CharacterName
                || strict.PasswordMd5 != source.PasswordMd5)
            {
                throw new InvalidDataException(
                    "Provisioniertes Benchmark-Credential verändert Identität oder lässt Auto-Create aktiv.");
            }

            var options = new FiestaLoadIdentityProvisionOptions
            {
                CredentialManifestPath = "manifest.json",
                OutputDirectory = Path.GetTempPath(),
                MaxConcurrency = 4,
                BatchPause = TimeSpan.FromMilliseconds(500),
                ClientOptions = new FiestaHeadlessProbeOptions()
            };
            if (options.MaxConcurrency is < 1 or > 8)
                throw new InvalidDataException("Provisioning-Concurrency liegt außerhalb des sicheren Bereichs.");

            return new FiestaLoadIdentityProvisionSelfTestResult(
                true,
                "LOAD IDENTITY PROVISION SELFTEST: PASS · Benchmark-Credentials deaktivieren Auto-Create und bewahren Login-/Char-Identität.");
        }
        catch (Exception ex)
        {
            return new FiestaLoadIdentityProvisionSelfTestResult(
                false,
                "LOAD IDENTITY PROVISION SELFTEST: FAIL · " + ex.Message);
        }
    }

    private static FiestaHeadlessProbeOptions CloneClientOptions(FiestaHeadlessProbeOptions source)
        => new()
        {
            LoginHost = source.LoginHost,
            LoginPort = source.LoginPort,
            WorldId = source.WorldId,
            ClientYear = source.ClientYear,
            ClientVersion = source.ClientVersion,
            ClientTag = source.ClientTag,
            FileHash = source.FileHash,
            ClientCaptureProfilePath = source.ClientCaptureProfilePath,
            ZoneTransferTemplatePath = source.ZoneTransferTemplatePath,
            CharacterCreateTemplatePath = source.CharacterCreateTemplatePath,
            AllowEmulatorWorldClientKeyFallback = false,
            AllowEmulatorSizedZoneTransfer = false,
            StepTimeout = source.StepTimeout,
            ZoneLoginTimeout = source.ZoneLoginTimeout,
            HoldDuration = TimeSpan.Zero
        };

    private static FiestaLoadClientCredential CloneCredential(
        FiestaLoadClientCredential source,
        bool createCharacterIfMissing)
        => new()
        {
            Username = source.Username,
            Password = source.Password,
            PasswordMd5 = source.PasswordMd5,
            CharacterName = source.CharacterName,
            Slot = source.Slot,
            CreateCharacterIfMissing = createCharacterIfMissing
        };

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class FiestaLoadIdentityProvisionOptions
{
    public string CredentialManifestPath { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public FiestaHeadlessProbeOptions ClientOptions { get; init; } = new();
    public int MaxConcurrency { get; init; } = 4;
    public TimeSpan BatchPause { get; init; } = TimeSpan.FromMilliseconds(500);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CredentialManifestPath)
            || !File.Exists(CredentialManifestPath))
        {
            throw new FileNotFoundException(
                "CredentialManifestPath wurde nicht gefunden.",
                CredentialManifestPath);
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
            throw new ArgumentException("OutputDirectory fehlt.");
        if (MaxConcurrency is < 1 or > 8)
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrency),
                "MaxConcurrency muss zwischen 1 und 8 liegen.");
        if (BatchPause < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BatchPause));

        ClientOptions.Validate();

        if (string.IsNullOrWhiteSpace(ClientOptions.ClientCaptureProfilePath))
            throw new ArgumentException(
                "Identity-Provisioning benötigt das capture-basierte ClientCaptureProfile.");
    }
}

public sealed class FiestaLoadIdentityProvisionResult
{
    public bool Success { get; init; }
    public int ProvisionedCount { get; init; }
    public int TotalCount { get; init; }
    public string ProvisionedManifestPath { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public IReadOnlyList<string> FailureSamples { get; init; } = Array.Empty<string>();
    public string Detail { get; init; } = string.Empty;
}

public readonly record struct FiestaLoadIdentityProvisionProgress(
    string Phase,
    int Completed,
    int Total,
    string Username,
    string Detail);

public readonly record struct FiestaLoadIdentityProvisionSelfTestResult(
    bool Success,
    string Detail);
