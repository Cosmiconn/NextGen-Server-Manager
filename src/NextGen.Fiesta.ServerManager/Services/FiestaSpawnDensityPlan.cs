using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>Offline SQL plan only. Never connects to SQL or moves a live character.</summary>
public static class FiestaSpawnDensityPlan
{
    public sealed record Point(string Map, int X, int Y, bool VerifiedInGame);

    public static string Generate(string manifestPath, string outputRoot, string? pointsPath = null)
    {
        var manifest = FiestaLoadCredentialManifest.Load(manifestPath);
        var clients = manifest.Clients.Take(1100).ToArray();
        if (clients.Length != 1100 || clients.Any(c => c.CreateCharacterIfMissing
            || !c.Username.StartsWith("r_", StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(c.CharacterName ?? "", @"^NG[A-Za-z0-9_]{1,38}$")))
            throw new InvalidDataException("1100 vorprovisionierte NG-Testcharaktere mit r_-Accounts erforderlich.");
        if (clients.Select(c => c.CharacterName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1100)
            throw new InvalidDataException("Doppelte Testcharaktere im Manifest.");
        Point[]? points = null;
        if (pointsPath != null)
        {
            points = JsonSerializer.Deserialize<Point[]>(File.ReadAllText(pointsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            ValidatePoints(points);
        }
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(outputRoot, "spawn-density-" + id);
        Directory.CreateDirectory(dir);
        var backup = "[dbo].[NG_SpawnBackup_" + id + "]";
        var preamble = new StringBuilder("USE [World00_Character];\nSET NOCOUNT ON;\nSET XACT_ABORT ON;\nSET LOCK_TIMEOUT 5000;\n");
        preamble.AppendLine("IF OBJECT_ID('tempdb..#Targets') IS NOT NULL DROP TABLE #Targets;");
        preamble.AppendLine("CREATE TABLE #Targets (Ordinal int PRIMARY KEY, Name nvarchar(40) COLLATE DATABASE_DEFAULT UNIQUE);");
        for (var i = 0; i < clients.Length; i++)
            preamble.AppendLine($"INSERT #Targets VALUES ({i}, N'{clients[i].CharacterName}');");
        preamble.AppendLine("IF (SELECT COUNT(*) FROM dbo.tCharacter c JOIN #Targets t ON c.sID=t.Name WHERE c.bDeleted=0) <> 1100 THROW 51000, 'Exactly 1100 active test characters required.', 1;");
        preamble.AppendLine("IF EXISTS (SELECT t.Name FROM #Targets t JOIN dbo.tCharacter c ON c.sID=t.Name AND c.bDeleted=0 GROUP BY t.Name HAVING COUNT(*)<>1) THROW 51000, 'Ambiguous character identity.', 1;");
        var inspect = preamble + """
SELECT t.Ordinal+1 AS ManifestIndex,c.nCharNo,c.sID,c.sLoginZone,c.nLoginZoneX,c.nLoginZoneY,c.nLoginZoneD
FROM dbo.tCharacter c JOIN #Targets t ON c.sID=t.Name WHERE c.bDeleted=0 ORDER BY t.Ordinal;
SELECT c.sLoginZone,c.nLoginZoneX,c.nLoginZoneY,COUNT(*) AS CharactersAtPosition
FROM dbo.tCharacter c JOIN #Targets t ON c.sID=t.Name WHERE c.bDeleted=0
GROUP BY c.sLoginZone,c.nLoginZoneX,c.nLoginZoneY ORDER BY COUNT(*) DESC;
""";
        Write(dir, "01-inspect.sql", inspect);
        Write(dir, "points.template.json", "[\n  { \"Map\": \"REPLACE_WITH_VERIFIED_MAP\", \"X\": 0, \"Y\": 0, \"VerifiedInGame\": false }\n]\n");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)));
        Write(dir, "README.txt", $"""
SPAWN DENSITY COMPARISON - Zone00 - plan {id}
Manifest SHA256: {hash}
First 1100 identities in original manifest order; no passwords exported.

1. Run 01-inspect.sql in SSMS against your TEST World00_Character database.
   It only reads character data (temporary target list in tempdb).
2. Supply at least two DISTINCT, in-game verified positions on the SAME map.
   Use points.template.json as format. First point is the concentrated start point.
   Do not invent a coordinate grid: bounds alone do not prove walkability.
   Map must already be the login map of ALL selected test characters.
   Click E again and select the completed JSON. Cancel creates inspection only.
3. With points supplied: fully stop Zone, WorldManager and Character services,
   after clients have logged out and position saves have finished. Keep Login closed
   to other clients. SQL cannot verify that the game services are offline.
   Run 02-snapshot.sql (Commit=1, OfflineConfirmed=1) ONCE. Keep its backup table.
4. Run 03-cluster.sql: preview defaults to rollback. Review positions, then set
   Commit=1 and OfflineConfirmed=1 to apply only X/Y for these 1100 characters.
5. Start the same services, use the SAME manifest and C diagnostic (1100, 2 seconds).
   Set 'B: zuerst #' to 0. Save trace, log delta and EXE hash as CLUSTER.
6. Shut down cleanly again, run 04-spread.sql, then repeat the IDENTICAL C run.
   Save trace, log delta and EXE hash as SPREAD. Do not switch Stock/Patch here.
7. Repeat CLUSTER/SPREAD in reversed order if feasible. Compare retries, 1669,
   ready/actual players, mob/NPC counts, failed identities and first-hit coordinates.
8. Stop services again; 05-restore.sql restores original X/Y, preserving the backup.
   Restore blocks if a character changed identity/map. It never changes map routing.

These are START positions, not proof of positions held during gameplay.
Verify actual arrivals: server repair, deaths, KQ/prison routing or skills may move
characters. If either layout is not retained, the comparison is inconclusive.
Keep map, binary, listener, start order, retry settings, mob/NPC population and
skill environment identical. Spread also changes visibility/nearby combat; a
better result is evidence of a local effect, not proof of a per-tile player limit.
Never call p_Char_GetAllData merely to inspect: the supplied DB procedure has
rebirth/state side effects. These scripts use direct SELECT instead.

SQL files are generated only; this tool executes NO database updates or tests.
{(points == null ? "STATUS: INSPECTION ONLY - verified positions are still required." : "STATUS: SQL PLAN GENERATED - SQL/runtime execution still requires local validation.")}
""");
        if (points == null) return dir;
        Write(dir, "points.json", JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true }));
        var map = points[0].Map;
        var guard = preamble + $"""
IF EXISTS (SELECT 1 FROM dbo.tCharacter c JOIN #Targets t ON c.sID=t.Name
 WHERE c.bDeleted=0 AND (c.sLoginZone IS NULL OR c.sLoginZone <> '{map}'))
 THROW 51000, 'Characters must already be on the selected map. No routing changes allowed.', 1;
""";
        var snapshot = $"""
IF OBJECT_ID(N'{backup}', N'U') IS NOT NULL THROW 51000, 'Backup exists: never overwrite.', 1;
SELECT t.Ordinal,c.nCharNo,c.nUserNo,c.sID,c.sLoginZone,c.nLoginZoneX,c.nLoginZoneY,c.nLoginZoneD
INTO {backup} FROM dbo.tCharacter c WITH (UPDLOCK,HOLDLOCK)
JOIN #Targets t ON c.sID=t.Name WHERE c.bDeleted=0;
IF @@ROWCOUNT <> 1100 THROW 51000, 'Snapshot count mismatch.', 1;
SELECT * FROM {backup} ORDER BY Ordinal;
""";
        Write(dir, "02-snapshot.sql", Transaction(guard, snapshot));
        var identityGuard = $"""
IF (SELECT COUNT(*) FROM {backup}) <> 1100 THROW 51000, 'Backup count mismatch.', 1;
IF (SELECT COUNT(*) FROM {backup} b JOIN #Targets t ON t.Ordinal=b.Ordinal AND t.Name=b.sID
 JOIN dbo.tCharacter c ON c.nCharNo=b.nCharNo AND c.nUserNo=b.nUserNo AND c.sID=b.sID
 AND c.sLoginZone=b.sLoginZone AND c.bDeleted=0) <> 1100
 THROW 51000, 'Identity/map mismatch; restore or apply refused.', 1;
""";
        foreach (var spread in new[] { false, true })
        {
            var sql = new StringBuilder(identityGuard);
            sql.AppendLine("IF OBJECT_ID('tempdb..#Desired') IS NOT NULL DROP TABLE #Desired;");
            sql.AppendLine("CREATE TABLE #Desired (Ordinal int PRIMARY KEY, X int NOT NULL, Y int NOT NULL);");
            for (var i = 0; i < clients.Length; i++)
            {
                var p = points[spread ? i % points.Length : 0];
                sql.AppendLine($"INSERT #Desired VALUES ({i},{p.X},{p.Y});");
            }
            sql.AppendLine($"SELECT b.sID,c.nLoginZoneX AS OldX,c.nLoginZoneY AS OldY,d.X AS NewX,d.Y AS NewY FROM {backup} b JOIN dbo.tCharacter c ON c.nCharNo=b.nCharNo JOIN #Desired d ON d.Ordinal=b.Ordinal ORDER BY b.Ordinal;");
            sql.AppendLine($"UPDATE c SET nLoginZoneX=d.X,nLoginZoneY=d.Y FROM dbo.tCharacter c JOIN {backup} b ON b.nCharNo=c.nCharNo JOIN #Desired d ON d.Ordinal=b.Ordinal;");
            sql.AppendLine("IF @@ROWCOUNT <> 1100 THROW 51000, 'Update count mismatch.', 1;");
            Write(dir, spread ? "04-spread.sql" : "03-cluster.sql", Transaction(guard, sql.ToString()));
        }
        Write(dir, "05-restore.sql", Transaction(guard, identityGuard + $"""
UPDATE c SET nLoginZoneX=b.nLoginZoneX,nLoginZoneY=b.nLoginZoneY
FROM dbo.tCharacter c JOIN {backup} b ON c.nCharNo=b.nCharNo;
IF @@ROWCOUNT <> 1100 THROW 51000, 'Restore count mismatch.', 1;
SELECT 'Original X/Y restored; backup retained.' AS Result;
"""));
        return dir;
    }

    private static string Transaction(string guard, string body) => $"""
-- Run with game services OFFLINE, after saves complete. Default is preview/rollback.
IF @@TRANCOUNT <> 0 THROW 51000, 'Use a session without an existing transaction.', 1;
DECLARE @Commit bit = 0, @OfflineConfirmed bit = 0;
IF @OfflineConfirmed <> 1 THROW 51000, 'Stop Zone, WorldManager and Character; confirm offline first.', 1;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 {guard}
 {body}
 IF @Commit = 1 BEGIN COMMIT; SELECT 'COMMITTED' AS Result; END
 ELSE BEGIN ROLLBACK; SELECT 'PREVIEW ONLY - rolled back' AS Result; END
END TRY
BEGIN CATCH
 IF @@TRANCOUNT > 0 ROLLBACK;
 THROW;
END CATCH;
""";

    public static void ValidatePoints(Point[]? points)
    {
        if (points == null || points.Length < 2 || points.Length > 1100
            || points.Any(p => p == null || !p.VerifiedInGame
                || !Regex.IsMatch(p.Map ?? "", @"^[A-Za-z0-9_]{1,16}$")
                || p.X <= 0 || p.Y <= 0 || p.X >= 0xFFFFFF || p.Y >= 0xFFFFFF)
            || points.Select(p => p.Map).Distinct(StringComparer.Ordinal).Count() != 1
            || points.Select(p => (p.X, p.Y)).Distinct().Count() != points.Length)
            throw new InvalidDataException("2..1100 verschiedene, im Spiel geprüfte positive X/Y-Positionen auf derselben Map erforderlich.");
    }

    public static void RunSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ng-spawn-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifest = new FiestaLoadCredentialManifest
            {
                Clients = Enumerable.Range(1, 1600).Select(i => new FiestaLoadClientCredential
                {
                    Username = $"r_ngt{i:D6}", CharacterName = $"NGT{i:D6}",
                    Password = "SELFTEST_SECRET_NOT_FOR_EXPORT", CreateCharacterIfMissing = false
                }).ToList()
            };
            var manifestPath = Path.Combine(root, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            var inspection = Generate(manifestPath, root);
            if (Directory.GetFiles(inspection, "*.sql").Length != 1)
                throw new InvalidOperationException("Unverified points must never generate update SQL.");
            var points = new[] { new Point("TestMap", 100, 100, true), new Point("TestMap", 200, 200, true), new Point("TestMap", 300, 300, true) };
            var path = Path.Combine(root, "points.json");
            File.WriteAllText(path, JsonSerializer.Serialize(points));
            var plan = Generate(manifestPath, root, path);
            var spread = File.ReadAllText(Path.Combine(plan, "04-spread.sql"));
            var cluster = File.ReadAllText(Path.Combine(plan, "03-cluster.sql"));
            if (Regex.Matches(spread, "INSERT #Desired VALUES").Count != 1100
                || !spread.Contains("INSERT #Desired VALUES (1099,200,200);")
                || !cluster.Contains("INSERT #Desired VALUES (1099,100,100);")
                || spread.Contains("NGT001101") || !spread.Contains("NGT001100")
                || !spread.Contains("@Commit bit = 0") || !spread.Contains("ROLLBACK")
                || !spread.Contains("Identity/map mismatch"))
                throw new InvalidOperationException("Density assignment or SQL guards incorrect.");
            if (Directory.GetFiles(plan).Any(f => File.ReadAllText(f).Contains("SELFTEST_SECRET_NOT_FOR_EXPORT")))
                throw new InvalidOperationException("Plan leaked credential secret.");
            foreach (var bad in new[] {
                new[] { points[0] }, new[] { points[0], points[0] },
                new[] { points[0], new Point("Other",200,200,true) },
                new[] { points[0], new Point("TestMap",200,200,false) },
                new[] { points[0], new Point("TestMap",-1,200,true) },
                new[] { points[0], new Point("x';DROP",200,200,true) } })
            {
                var rejected = false;
                try { ValidatePoints(bad); } catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Invalid point set accepted.");
            }
            manifest.Clients.RemoveRange(1000, 600);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            var missingRejected = false;
            try { Generate(manifestPath, root, path); } catch (InvalidDataException) { missingRejected = true; }
            if (!missingRejected) throw new InvalidOperationException("Incomplete cohort accepted.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Write(string dir, string name, string text) =>
        File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
}
