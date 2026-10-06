using System.IO;
using System.Text;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Live tail for NA2016 service logs. Watches the complete service subtree (not only the
/// service root), so DebugMessage/Logs/Error subfolders and rotated files are covered.
/// FileSystemWatcher provides low latency and a polling fallback catches missed events.
/// </summary>
public sealed class LiveLogMonitorService : IDisposable
{
    private sealed record DirectoryTarget(string ServiceName, int? ZoneNumber, string DirectoryPath);

    private sealed class WatchedFile
    {
        public required string ServiceName { get; init; }
        public required int? ZoneNumber { get; init; }
        public required string Path { get; init; }
        public long Position { get; set; }
        public string PartialLine { get; set; } = string.Empty;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly Dictionary<string, WatchedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DirectoryTarget> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private readonly LogDiscoveryService _discovery = new();
    private System.Threading.Timer? _pollTimer;
    private bool _disposed;
    private bool _readExistingTail;

    public event Action<LiveLogLine>? LineReceived;
    public bool IsRunning { get; private set; }
    public int WatchedFileCount { get { lock (_gate) return _files.Count; } }

    public void Start(IEnumerable<(string ServiceName, int? ZoneNumber, string DirectoryPath)> directories, bool readExistingTail = false)
    {
        Stop();
        _readExistingTail = readExistingTail;

        lock (_gate)
        {
            foreach (var item in directories.Where(x => Directory.Exists(x.DirectoryPath)))
            {
                var dir = Path.GetFullPath(item.DirectoryPath);
                var target = new DirectoryTarget(item.ServiceName, item.ZoneNumber, dir);
                _directories[dir] = target;

                foreach (var path in _discovery.FindLogs(dir))
                    EnsureTrackedUnsafe(target, path);
            }

            foreach (var target in _directories.Values)
            {
                var watcher = new FileSystemWatcher(target.DirectoryPath)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                    Filter = "*.*",
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = false
                };
                watcher.Changed += (_, e) => OnFileSignal(target, e.FullPath);
                watcher.Created += (_, e) => OnFileSignal(target, e.FullPath);
                watcher.Renamed += (_, e) => OnFileSignal(target, e.FullPath);
                watcher.Error += (_, _) => QueueReadAll();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }

            _pollTimer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            IsRunning = true;
        }

        if (readExistingTail) QueueReadAll();
    }

    public void Stop()
    {
        lock (_gate)
        {
            IsRunning = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            _files.Clear();
            _directories.Clear();
        }
    }

    private void Poll()
    {
        List<DirectoryTarget> targets;
        lock (_gate)
        {
            if (!IsRunning) return;
            targets = _directories.Values.ToList();
        }

        // Rediscover nested/rotated logs in case FileSystemWatcher dropped an event.
        foreach (var target in targets)
        {
            try
            {
                foreach (var path in _discovery.FindLogs(target.DirectoryPath))
                    OnFileSignal(target, path);
            }
            catch { }
        }
        QueueReadAll();
    }

    private void OnFileSignal(DirectoryTarget target, string path)
    {
        if (!_discovery.IsCandidateLog(target.DirectoryPath, path)) return;
        lock (_gate)
        {
            if (!IsRunning) return;
            EnsureTrackedUnsafe(target, path);
        }
        QueueRead(path);
    }

    private void EnsureTrackedUnsafe(DirectoryTarget target, string path)
    {
        var full = Path.GetFullPath(path);
        if (_files.ContainsKey(full) || !File.Exists(full)) return;
        long position = 0;
        try
        {
            var length = new FileInfo(full).Length;
            position = _readExistingTail ? Math.Max(0, length - 128 * 1024) : length;
        }
        catch { }

        _files[full] = new WatchedFile
        {
            ServiceName = target.ServiceName,
            ZoneNumber = target.ZoneNumber,
            Path = full,
            Position = position
        };
    }

    private void QueueReadAll()
    {
        string[] paths;
        lock (_gate)
        {
            if (!IsRunning) return;
            paths = _files.Keys.ToArray();
        }
        foreach (var path in paths) QueueRead(path);
    }

    private void QueueRead(string path)
    {
        WatchedFile? file;
        lock (_gate)
        {
            if (!IsRunning || !_files.TryGetValue(Path.GetFullPath(path), out file)) return;
        }
        _ = Task.Run(() => ReadNewLinesAsync(file));
    }

    private async Task ReadNewLinesAsync(WatchedFile file)
    {
        if (!await file.Gate.WaitAsync(0)) return;
        try
        {
            if (!File.Exists(file.Path)) return;
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Position > stream.Length)
            {
                file.Position = 0;
                file.PartialLine = string.Empty;
            }
            if (file.Position == stream.Length) return;

            stream.Seek(file.Position, SeekOrigin.Begin);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            file.Position = stream.Position;
            var bytes = memory.ToArray();
            if (bytes.Length == 0) return;

            var text = file.PartialLine + DecodeBytes(bytes);
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var parts = normalized.Split('\n');
            var completeCount = normalized.EndsWith('\n') ? parts.Length : parts.Length - 1;
            for (var i = 0; i < completeCount; i++)
            {
                var line = parts[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                LineReceived?.Invoke(new LiveLogLine(ParseTimestamp(line) ?? DateTime.Now, file.ServiceName, file.ZoneNumber, file.Path, line));
            }
            file.PartialLine = normalized.EndsWith('\n') ? string.Empty : parts[^1];
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { file.Gate.Release(); }
    }

    private static string DecodeBytes(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static DateTime? ParseTimestamp(string line)
    {
        var marker = line.IndexOf("20", StringComparison.Ordinal);
        if (marker < 0 || line.Length < marker + 19) return null;
        var candidate = line.Substring(marker, 19);
        return DateTime.TryParse(candidate, out var timestamp) ? timestamp : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
