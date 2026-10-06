namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Discovers NA2016 log files below a service directory. The original MVP only checked a
/// handful of top-level file names. Real NA2016 installations often write into nested
/// DebugMessage/Log/Logs/Error folders and use additional text-like extensions.
/// </summary>
public sealed class LogDiscoveryService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".err", ".out", ".trace", ".dbg"
    };

    private static readonly string[] PreferredNames =
    {
        "Message.txt", "Dbg.txt", "Debug.txt", "Error.txt", "Errors.txt",
        "Exception.txt", "Crash.txt", "Assert.txt"
    };

    private static readonly string[] FileNameTokens =
    {
        "msg", "message", "dbg", "debug", "log", "error", "err", "warn", "warning",
        "exception", "crash", "assert", "fault", "trace", "packet", "connect", "network"
    };

    private static readonly string[] LogDirectoryTokens =
    {
        "log", "logs", "debug", "debugmessage", "debugmessages", "message", "messages",
        "error", "errors", "crash", "crashes", "trace", "traces", "dump", "dumps"
    };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nextgen-cache", ".git", "bin", "obj", "backup", "backups"
    };

    public IReadOnlyList<string> FindLogs(string serviceDirectory, int maxDepth = 8)
    {
        if (!Directory.Exists(serviceDirectory)) return Array.Empty<string>();

        var root = Path.GetFullPath(serviceDirectory);
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Preserve the classic top-level names first.
        foreach (var preferred in PreferredNames)
        {
            var direct = Path.Combine(root, preferred);
            if (File.Exists(direct)) all.Add(direct);
        }

        foreach (var file in EnumerateFilesSafe(root, maxDepth))
        {
            if (IsCandidateLog(root, file)) all.Add(Path.GetFullPath(file));
        }

        return all
            .OrderByDescending(SafeLastWriteUtc)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsCandidateLog(string serviceDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var ext = Path.GetExtension(path);
        if (!TextExtensions.Contains(ext)) return false;

        var root = Path.GetFullPath(serviceDirectory);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        if (ContainsIgnoredDirectory(root, full)) return false;

        var name = Path.GetFileName(full);
        if (PreferredNames.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase))) return true;

        // A file inside a known log/debug directory is considered a log even if the
        // filename itself is only a date or sequence number (common on live servers).
        if (IsInLogDirectory(root, full)) return true;

        var stem = Path.GetFileNameWithoutExtension(name);
        return FileNameTokens.Any(token => stem.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> Tail(string path, int maxLines = 500)
    {
        if (!File.Exists(path) || maxLines <= 0) return Array.Empty<string>();

        // Stream with FileShare.ReadWrite/Delete so active Fiesta services can continue
        // writing/rotating while the manager reads. A bounded queue prevents very large
        // historical logs from being loaded completely into RAM.
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
            var queue = new Queue<string>(maxLines + 1);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                queue.Enqueue(line);
                if (queue.Count > maxLines) queue.Dequeue();
            }
            return queue.ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, int maxDepth)
    {
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory); }
            catch { files = Array.Empty<string>(); }

            foreach (var file in files)
                yield return file;

            if (depth >= maxDepth) continue;

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(directory); }
            catch { dirs = Array.Empty<string>(); }

            foreach (var child in dirs)
            {
                var name = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (IgnoredDirectories.Contains(name)) continue;
                pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static bool IsInLogDirectory(string root, string file)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(file) ?? root);
        if (relative == ".") return false;
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => LogDirectoryTokens.Any(token => part.Equals(token, StringComparison.OrdinalIgnoreCase)
            || part.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ContainsIgnoredDirectory(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Take(Math.Max(0, parts.Length - 1)).Any(IgnoredDirectories.Contains);
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
