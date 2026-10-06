using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Generates an OFFLINE test-account plan. It never opens SQL connections.
/// The generated SQL calls the original NA2016 Account.dbo.usp_User_insert procedure whose
/// full signature is present in the supplied Account.bak. Characters are intentionally NOT
/// inserted into World00_Character; the headless client creates them through captured CH5/1.
/// </summary>
public sealed class FiestaLoadIdentityGenerator
{
    private static readonly Regex SafeName = new(
        @"^[A-Za-z0-9_]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FiestaLoadIdentityGenerationResult Generate(FiestaLoadIdentityGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var clients = new List<FiestaLoadClientCredential>(options.Count);
        for (var i = 1; i <= options.Count; i++)
        {
            var suffix = i.ToString("D6");
            var username = options.UsernamePrefix + suffix;
            var characterName = options.CharacterPrefix + suffix;
            var passwordMd5 = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            clients.Add(new FiestaLoadClientCredential
            {
                Username = username,
                PasswordMd5 = passwordMd5,
                CharacterName = characterName,
                Slot = options.CharacterSlot,
                CreateCharacterIfMissing = true
            });
        }

        var manifest = new FiestaLoadCredentialManifest
        {
            Clients = clients
        };
        manifest.Validate();

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var manifestPath = Path.Combine(outputDirectory, $"load-credentials-{options.Count}-{stamp}.json");
        var sqlPath = Path.Combine(outputDirectory, $"create-load-accounts-{options.Count}-{stamp}.sql");

        var manifestJson = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, manifestJson + Environment.NewLine, new UTF8Encoding(false));

        var sql = BuildSql(options, clients);
        File.WriteAllText(sqlPath, sql, new UTF8Encoding(false));

        return new FiestaLoadIdentityGenerationResult
        {
            Success = true,
            Count = clients.Count,
            SqlPath = sqlPath,
            CredentialManifestPath = manifestPath,
            SqlSha256 = Sha256File(sqlPath),
            ManifestSha256 = Sha256File(manifestPath),
            Detail =
                $"LOAD IDENTITIES: GENERATED · {clients.Count:N0} Accounts · " +
                $"SQL {Path.GetFileName(sqlPath)} · Credentials {Path.GetFileName(manifestPath)}. " +
                "Keine Datenbank wurde verändert."
        };
    }

    public static FiestaLoadIdentitySelfTestResult RunSelfTest()
    {
        try
        {
            var options = new FiestaLoadIdentityGenerationOptions
            {
                Count = 3,
                UsernamePrefix = "ngt",
                CharacterPrefix = "NGT",
                OutputDirectory = Path.GetTempPath(),
                AccountDatabase = "Account"
            };

            var clients = new List<FiestaLoadClientCredential>();
            for (var i = 1; i <= options.Count; i++)
            {
                var suffix = i.ToString("D6");
                clients.Add(new FiestaLoadClientCredential
                {
                    Username = options.UsernamePrefix + suffix,
                    PasswordMd5 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("selftest-" + i)))[..32].ToLowerInvariant(),
                    CharacterName = options.CharacterPrefix + suffix,
                    Slot = 0,
                    CreateCharacterIfMissing = true
                });
            }

            var sql = BuildSql(options, clients);
            if (!sql.Contains("[Account].[dbo].[usp_User_insert]", StringComparison.Ordinal)
                || !sql.Contains("BEGIN TRANSACTION", StringComparison.Ordinal)
                || !sql.Contains("ROLLBACK TRANSACTION", StringComparison.Ordinal)
                || sql.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
                || clients.Any(x => !x.CreateCharacterIfMissing)
                || clients.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            {
                throw new InvalidDataException("Generator verletzte Stored-Procedure-/Transaktions-/Eindeutigkeitsregeln.");
            }

            return new FiestaLoadIdentitySelfTestResult(
                true,
                "LOAD IDENTITY SELFTEST: PASS · original usp_User_insert only, transaction guard, unique manifest.");
        }
        catch (Exception ex)
        {
            return new FiestaLoadIdentitySelfTestResult(
                false,
                "LOAD IDENTITY SELFTEST: FAIL · " + ex.Message);
        }
    }

    private static string BuildSql(
        FiestaLoadIdentityGenerationOptions options,
        IReadOnlyList<FiestaLoadClientCredential> clients)
    {
        var db = options.AccountDatabase;
        var sb = new StringBuilder(512 * 1024);

        sb.AppendLine("/*");
        sb.AppendLine("  NextGen Fiesta Server Manager - isolated load-test accounts");
        sb.AppendLine("  GENERATED OFFLINE. Review before execution.");
        sb.AppendLine("  Uses ONLY the original NA2016 Account.dbo.usp_User_insert procedure.");
        sb.AppendLine("  It does NOT create characters and does NOT modify Zone/World binaries.");
        sb.AppendLine("  Run only against the local/test NA2016 SQL instance intended for the load test.");
        sb.AppendLine("*/");
        sb.AppendLine("SET NOCOUNT ON;");
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine();
        sb.AppendLine($"IF DB_ID(N'{EscapeSqlLiteral(db)}') IS NULL");
        sb.AppendLine($"    THROW 51000, 'Required database {EscapeSqlLiteral(db)} was not found.', 1;");
        sb.AppendLine($"IF OBJECT_ID(N'{EscapeSqlLiteral(db)}.dbo.usp_User_insert', N'P') IS NULL");
        sb.AppendLine("    THROW 51001, 'Original usp_User_insert procedure was not found. Nothing was changed.', 1;");
        sb.AppendLine();
        sb.AppendLine("BEGIN TRY");
        sb.AppendLine("    BEGIN TRANSACTION;");
        sb.AppendLine("    DECLARE @ret int;");
        sb.AppendLine("    DECLARE @userNo int;");
        sb.AppendLine();

        foreach (var client in clients)
        {
            sb.AppendLine("    SET @ret = 0;");
            sb.AppendLine("    SET @userNo = 0;");
            sb.AppendLine($"    EXEC @ret = [{db}].[dbo].[usp_User_insert]");
            sb.AppendLine($"         @userID = N'{EscapeSqlLiteral(client.Username)}',");
            sb.AppendLine($"         @userPW = N'{EscapeSqlLiteral(client.PasswordMd5!)}',");
            sb.AppendLine("         @nAffiliateID = NULL,");
            sb.AppendLine("         @szAffilateSubID = NULL,");
            sb.AppendLine("         @szCountryCode = NULL,");
            sb.AppendLine("         @userName = N'NGLoad',");
            sb.AppendLine("         @userIP = N'127.0.0.1',");
            sb.AppendLine("         @eMail = N'',");
            sb.AppendLine("         @juminNo = N'',");
            sb.AppendLine("         @phoneNo = N'',");
            sb.AppendLine("         @zipNo = 0,");
            sb.AppendLine("         @address = N'',");
            sb.AppendLine("         @isMail = 0,");
            sb.AppendLine("         @channel = N'ONSON',");
            sb.AppendLine("         @isOption = 1,");
            sb.AppendLine("         @userNo = @userNo OUTPUT;");
            sb.AppendLine("    IF (@ret <> 0 OR @userNo <= 0)");
            sb.AppendLine($"        THROW 51002, 'usp_User_insert failed while creating {EscapeSqlLiteral(client.Username)}; transaction rolled back.', 1;");
            sb.AppendLine();
        }

        sb.AppendLine("    COMMIT TRANSACTION;");
        sb.AppendLine($"    PRINT 'NextGen load-account creation committed: {clients.Count} accounts.';");
        sb.AppendLine("END TRY");
        sb.AppendLine("BEGIN CATCH");
        sb.AppendLine("    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        sb.AppendLine("    THROW;");
        sb.AppendLine("END CATCH;");
        return sb.ToString();
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class FiestaLoadIdentityGenerationOptions
{
    public int Count { get; init; } = 1600;
    public string UsernamePrefix { get; init; } = "ngl";
    public string CharacterPrefix { get; init; } = "NGL";
    public byte CharacterSlot { get; init; }
    public string AccountDatabase { get; init; } = "Account";
    public string OutputDirectory { get; init; } = string.Empty;

    public void Validate()
    {
        if (Count is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(Count), "Count muss zwischen 1 und 2000 liegen.");
        if (!SafeIdentifier(UsernamePrefix) || UsernamePrefix.Length + 6 > 20)
            throw new ArgumentException("UsernamePrefix darf nur A-Z/a-z/0-9/_ enthalten und mit 6-stelliger Nummer maximal 20 Zeichen ergeben.");
        if (!SafeIdentifier(CharacterPrefix) || CharacterPrefix.Length + 6 > 16)
            throw new ArgumentException("CharacterPrefix darf nur A-Z/a-z/0-9/_ enthalten und mit 6-stelliger Nummer maximal 16 Zeichen ergeben.");
        if (CharacterSlot > 10)
            throw new ArgumentOutOfRangeException(nameof(CharacterSlot));
        if (!SafeIdentifier(AccountDatabase) || AccountDatabase.Length > 64)
            throw new ArgumentException("AccountDatabase enthält ungültige Zeichen.");
        if (string.IsNullOrWhiteSpace(OutputDirectory))
            throw new ArgumentException("OutputDirectory fehlt.");
    }

    private static bool SafeIdentifier(string value)
        => !string.IsNullOrWhiteSpace(value)
           && Regex.IsMatch(value, @"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
}

public sealed class FiestaLoadIdentityGenerationResult
{
    public bool Success { get; init; }
    public int Count { get; init; }
    public string SqlPath { get; init; } = string.Empty;
    public string CredentialManifestPath { get; init; } = string.Empty;
    public string SqlSha256 { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public readonly record struct FiestaLoadIdentitySelfTestResult(bool Success, string Detail);
