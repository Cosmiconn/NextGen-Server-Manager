using System.Text;
using System.Text.Json;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Captures a read-only checkpoint of every recursively discovered NA2016 text log and
/// later analyzes only bytes written after that checkpoint. This avoids treating historic
/// server errors as evidence from the certified Zone pool runtime test.
/// </summary>
public sealed class ZonePoolRuntimeLogDeltaAudit
{
    private const int DefaultMaxDeltaBytesPerFile = 4 * 1024 * 1024;

    private static readonly HashSet<string> BlockingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NG-CORE-0001", // FATAL ERROR
        "NG-CORE-0002", // ASSERT
        "NG-CORE-0003", // Unhandled Exception
        "NG-PROTO-0003", // Packet Too Long
        "NG-ZONE-0014", // BlockInfo exhausted
        "NG-ZONE-0015", // Too many mob
        "NG-ZONE-0020", // Too many npc
        "NG-ZONE-0021", // MapCluster exhausted
        "NG-ZONE-0022"  // BlockDistribute exhausted
    };

    private static readonly string[] BlockingRawPatterns =
    {
        "access violation",
        "0xc0000005",
        "heap corruption",
        "stack overflow",
        "out of memory",
        "bad_alloc",
        "bad allocation",
        "fail to player quest bf alloc",
        "too many player"
    };

    private readonly LogDiscoveryService _discovery = new();
    private readonly LogAnalyzer _analyzer = new();

    public ZonePoolLogCheckpointResult SaveCheckpoint(string serverRoot, string checkpointPath)
    {
        if (string.IsNullOrWhiteSpace(serverRoot) || !Directory.Exists(serverRoot))
            return CheckpointFailed("Server-Root wurde nicht gefunden.");
        if (string.IsNullOrWhiteSpace(checkpointPath))
            return CheckpointFailed("Checkpoint-Pfad fehlt.");

        string root;
        string output;
        try
        {
            root = NormalizeDirectory(serverRoot);
            output = Path.GetFullPath(checkpointPath);
        }
        catch (Exception ex)
        {
            return CheckpointFailed("Pfadauflösung fehlgeschlagen: " + ex.Message);
        }

        if (IsInsideRoot(root, output))
            return CheckpointFailed("Checkpoint muss außerhalb des Server-Roots liegen.");
        if (File.Exists(output))
            return CheckpointFailed("Checkpoint-Datei existiert bereits; automatisches Überschreiben ist verboten.");

        var logs = _discovery.FindLogs(root, maxDepth: 8);
        var entries = new List<ZonePoolLogCheckpointEntry>(logs.Count);
        foreach (var path in logs)
        {
            try
            {
                var info = new FileInfo(path);
                entries.Add(new ZonePoolLogCheckpointEntry
                {
                    Path = Path.GetFullPath(path),
                    Length = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc
                });
            }
            catch (Exception ex)
            {
                return CheckpointFailed($"Logdatei konnte nicht sicher checkpointed werden: {path}: {ex.Message}");
            }
        }

        var checkpoint = new ZonePoolLogCheckpoint
        {
            FormatVersion = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            ServerRoot = root,
            MaxDepth = 8,
            Files = entries.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        try
        {
            var directory = Path.GetDirectoryName(output);
            if (string.IsNullOrWhiteSpace(directory))
                return CheckpointFailed("Checkpoint-Ausgabeverzeichnis konnte nicht bestimmt werden.");
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.SerializeToUtf8Bytes(checkpoint, new JsonSerializerOptions { WriteIndented = true });
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
            stream.Write(json, 0, json.Length);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            return CheckpointFailed("Checkpoint konnte nicht geschrieben werden: " + ex.Message);
        }

        return new ZonePoolLogCheckpointResult
        {
            Success = true,
            CheckpointPath = output,
            ServerRoot = root,
            CreatedUtc = checkpoint.CreatedUtc,
            FileCount = checkpoint.Files.Count,
            Detail = $"LOG CHECKPOINT OK: {checkpoint.Files.Count} rekursiv erkannte Logdatei(en) checkpointed; Serverdateien unverändert."
        };
    }

    public ZonePoolLogDeltaAuditResult Audit(string serverRoot, string checkpointPath, int maxDeltaBytesPerFile = DefaultMaxDeltaBytesPerFile)
    {
        if (string.IsNullOrWhiteSpace(serverRoot) || !Directory.Exists(serverRoot))
            return AuditFailed("Server-Root wurde nicht gefunden.");
        if (string.IsNullOrWhiteSpace(checkpointPath) || !File.Exists(checkpointPath))
            return AuditFailed("Checkpoint-Datei wurde nicht gefunden.");

        maxDeltaBytesPerFile = Math.Clamp(maxDeltaBytesPerFile, 64 * 1024, 64 * 1024 * 1024);

        string root;
        string checkpointFull;
        try
        {
            root = NormalizeDirectory(serverRoot);
            checkpointFull = Path.GetFullPath(checkpointPath);
        }
        catch (Exception ex)
        {
            return AuditFailed("Pfadauflösung fehlgeschlagen: " + ex.Message);
        }

        if (IsInsideRoot(root, checkpointFull))
            return AuditFailed("Checkpoint liegt innerhalb des Server-Roots und ist daher für den Audit unzulässig.");

        ZonePoolLogCheckpoint? checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<ZonePoolLogCheckpoint>(File.ReadAllBytes(checkpointFull));
        }
        catch (Exception ex)
        {
            return AuditFailed("Checkpoint konnte nicht gelesen werden: " + ex.Message);
        }

        if (checkpoint is null || checkpoint.FormatVersion != 1 || !PathEquals(checkpoint.ServerRoot, root))
            return AuditFailed("Checkpoint gehört nicht zu diesem Server-Root oder besitzt ein unbekanntes Format.");

        var oldFiles = checkpoint.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var currentPaths = _discovery.FindLogs(root, checkpoint.MaxDepth);
        var currentFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in currentPaths)
        {
            try { currentFiles[Path.GetFullPath(path)] = new FileInfo(path); }
            catch { }
        }

        var allPaths = new HashSet<string>(oldFiles.Keys, StringComparer.OrdinalIgnoreCase);
        allPaths.UnionWith(currentFiles.Keys);

        var findings = new List<ZonePoolLogDeltaFinding>();
        var evidenceGaps = new List<string>();
        var changedFiles = 0;
        var newFiles = 0;
        var rotatedOrTruncatedFiles = 0;
        long totalDeltaBytes = 0;
        var incomplete = false;

        foreach (var path in allPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var existed = oldFiles.TryGetValue(path, out var old);
            var existsNow = currentFiles.TryGetValue(path, out var current);

            if (existed && !existsNow)
            {
                incomplete = true;
                evidenceGaps.Add("Seit Checkpoint verschwunden/rotiert: " + path);
                continue;
            }
            if (!existsNow || current is null) continue;

            long offset;
            long available;
            var rotation = false;

            if (!existed || old is null)
            {
                // A newly discovered file is relevant only when it was written around/after
                // the checkpoint. Older untouched files can appear due to directory races.
                if (current.LastWriteTimeUtc < checkpoint.CreatedUtc.UtcDateTime.AddSeconds(-2))
                    continue;
                offset = 0;
                available = current.Length;
                newFiles++;
            }
            else if (current.Length > old.Length)
            {
                offset = old.Length;
                available = current.Length - old.Length;
            }
            else if (current.Length < old.Length)
            {
                // Truncation/rotation: old byte offset is no longer meaningful. Read the new
                // file from zero, but force REVIEW because continuity cannot be proven.
                offset = 0;
                available = current.Length;
                rotation = true;
                rotatedOrTruncatedFiles++;
                incomplete = true;
                evidenceGaps.Add("Log wurde seit Checkpoint gekürzt/ersetzt: " + path);
            }
            else
            {
                if (current.LastWriteTimeUtc > old.LastWriteUtc.AddSeconds(1))
                {
                    incomplete = true;
                    evidenceGaps.Add("Log änderte sich ohne Längenänderung; exakter Delta-Bereich unbestimmbar: " + path);
                }
                continue;
            }

            if (available <= 0) continue;
            changedFiles++;
            totalDeltaBytes += available;

            var toRead = (int)Math.Min(available, maxDeltaBytesPerFile);
            var truncated = available > maxDeltaBytesPerFile;
            if (truncated)
            {
                incomplete = true;
                evidenceGaps.Add($"Logdelta überschreitet {maxDeltaBytesPerFile:N0} Byte und wurde begrenzt: {path} ({available:N0} Byte neu)");
            }

            string deltaText;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (offset > stream.Length)
                {
                    incomplete = true;
                    evidenceGaps.Add("Log schrumpfte während des Audits: " + path);
                    continue;
                }
                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[toRead];
                var readTotal = 0;
                while (readTotal < buffer.Length)
                {
                    var read = stream.Read(buffer, readTotal, buffer.Length - readTotal);
                    if (read <= 0) break;
                    readTotal += read;
                }
                deltaText = DecodeLogBytes(buffer.AsSpan(0, readTotal));
            }
            catch (Exception ex)
            {
                incomplete = true;
                evidenceGaps.Add($"Logdelta konnte nicht gelesen werden: {path}: {ex.Message}");
                continue;
            }

            var lines = SplitLines(deltaText);
            if (lines.Count == 0) continue;

            foreach (var issue in _analyzer.Analyze(lines, path))
            {
                findings.Add(new ZonePoolLogDeltaFinding
                {
                    Source = path,
                    Severity = issue.Severity.ToString(),
                    Code = issue.Code ?? string.Empty,
                    Title = issue.Title,
                    Evidence = issue.Evidence,
                    Blocking = issue.Code is not null && BlockingCodes.Contains(issue.Code)
                });
            }

            foreach (var line in lines)
            {
                foreach (var pattern in BlockingRawPatterns)
                {
                    if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
                    findings.Add(new ZonePoolLogDeltaFinding
                    {
                        Source = path,
                        Severity = "Critical",
                        Code = "NG-ZONEPOOL-RUNTIME",
                        Title = "Harter Runtime-Sicherheitsindikator",
                        Evidence = line.Trim(),
                        Blocking = true
                    });
                    break;
                }
            }

            if (rotation || truncated)
            {
                findings.Add(new ZonePoolLogDeltaFinding
                {
                    Source = path,
                    Severity = "Warning",
                    Code = "NG-LOG-DELTA",
                    Title = "Logkontinuität muss manuell geprüft werden",
                    Evidence = rotation ? "Log wurde rotiert/gekürzt." : "Logdelta wurde wegen Größenlimit begrenzt.",
                    Blocking = false
                });
            }
        }

        // Deduplicate identical rule/evidence hits while retaining source distinction.
        findings = findings
            .GroupBy(x => $"{x.Source}|{x.Code}|{x.Evidence}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderByDescending(x => x.Blocking)
            .ThenBy(x => x.Severity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var blockers = findings.Count(x => x.Blocking);
        var reviewFindings = findings.Count(x => !x.Blocking &&
            (x.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)
             || x.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
             || x.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)));

        var status = blockers > 0
            ? "BLOCKED"
            : incomplete || reviewFindings > 0
                ? "REVIEW"
                : "CLEAN";

        return new ZonePoolLogDeltaAuditResult
        {
            Success = true,
            Status = status,
            Clean = status == "CLEAN",
            Blocked = status == "BLOCKED",
            ReviewRequired = status == "REVIEW",
            CheckpointCreatedUtc = checkpoint.CreatedUtc,
            ServerRoot = root,
            CheckpointPath = checkpointFull,
            ChangedFileCount = changedFiles,
            NewFileCount = newFiles,
            RotatedOrTruncatedFileCount = rotatedOrTruncatedFiles,
            TotalDeltaBytes = totalDeltaBytes,
            EvidenceComplete = !incomplete,
            Findings = findings,
            EvidenceGaps = evidenceGaps,
            Detail = status switch
            {
                "BLOCKED" => $"LOG DELTA BLOCKED: {blockers} harter Befund in ausschließlich seit dem Checkpoint hinzugekommenen Logdaten.",
                "REVIEW" => $"LOG DELTA REVIEW: keine harte Patch-Crash-Signatur, aber {reviewFindings} Warn-/Fehlerbefund(e) oder {evidenceGaps.Count} Evidenzlücke(n) müssen geprüft werden.",
                _ => $"LOG DELTA CLEAN: {changedFiles} geänderte Logdatei(en), {totalDeltaBytes:N0} neue Byte, keine neuen Warn-/Fehler-/Crashindikatoren."
            }
        };
    }

    private static string DecodeLogBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        // Fiesta diagnostics are overwhelmingly ASCII-compatible. UTF-8 replacement
        // characters do not affect the ASCII error signatures/rules used here.
        return Encoding.UTF8.GetString(bytes);
    }

    private static IReadOnlyList<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsInsideRoot(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static ZonePoolLogCheckpointResult CheckpointFailed(string detail)
        => new() { Success = false, Detail = detail };

    private static ZonePoolLogDeltaAuditResult AuditFailed(string detail)
        => new() { Success = false, Status = "BLOCKED", Blocked = true, Detail = detail };
}

public sealed class ZonePoolLogCheckpoint
{
    public int FormatVersion { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public string ServerRoot { get; init; } = string.Empty;
    public int MaxDepth { get; init; } = 8;
    public IReadOnlyList<ZonePoolLogCheckpointEntry> Files { get; init; } = Array.Empty<ZonePoolLogCheckpointEntry>();
}

public sealed class ZonePoolLogCheckpointEntry
{
    public string Path { get; init; } = string.Empty;
    public long Length { get; init; }
    public DateTime LastWriteUtc { get; init; }
}

public sealed class ZonePoolLogCheckpointResult
{
    public bool Success { get; init; }
    public string CheckpointPath { get; init; } = string.Empty;
    public string ServerRoot { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public int FileCount { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed class ZonePoolLogDeltaAuditResult
{
    public bool Success { get; init; }
    public string Status { get; init; } = "BLOCKED";
    public bool Clean { get; init; }
    public bool Blocked { get; init; }
    public bool ReviewRequired { get; init; }
    public bool EvidenceComplete { get; init; }
    public DateTimeOffset CheckpointCreatedUtc { get; init; }
    public string ServerRoot { get; init; } = string.Empty;
    public string CheckpointPath { get; init; } = string.Empty;
    public int ChangedFileCount { get; init; }
    public int NewFileCount { get; init; }
    public int RotatedOrTruncatedFileCount { get; init; }
    public long TotalDeltaBytes { get; init; }
    public IReadOnlyList<ZonePoolLogDeltaFinding> Findings { get; init; } = Array.Empty<ZonePoolLogDeltaFinding>();
    public IReadOnlyList<string> EvidenceGaps { get; init; } = Array.Empty<string>();
    public string Detail { get; init; } = string.Empty;
}

public sealed class ZonePoolLogDeltaFinding
{
    public string Source { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public bool Blocking { get; init; }
}
