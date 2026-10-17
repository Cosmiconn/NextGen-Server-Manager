using System.Diagnostics;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only, PID/start-time-bound resource samples. CPU is percent of one logical
/// core (process CPU can exceed 100%). It is not a gameplay tick/packet latency.
/// A failed sample contains an error rather than manufactured zero measurements.
/// </summary>
public sealed class LoadProcessSampler
{
    private readonly int _pid;
    private readonly DateTime _startUtc;
    private long? _lastTimestamp;
    private TimeSpan _lastCpu;
    private Dictionary<int, TimeSpan> _lastThreadCpu = new();

    public LoadProcessSampler(int processId)
    {
        using var process = Process.GetProcessById(processId);
        _pid = processId;
        _startUtc = process.StartTime.ToUniversalTime();
    }

    public LoadProcessSample Read()
    {
        var at = DateTimeOffset.UtcNow;
        try
        {
            using var process = Process.GetProcessById(_pid);
            if (process.StartTime.ToUniversalTime() != _startUtc || process.HasExited)
                throw new InvalidOperationException("Process identity changed or exited.");
            var timestamp = Stopwatch.GetTimestamp();
            var cpu = process.TotalProcessorTime;
            var threads = new Dictionary<int, TimeSpan>();
            string? threadError = null;
            foreach (ProcessThread thread in process.Threads)
            {
                using (thread)
                {
                    try { threads[thread.Id] = thread.TotalProcessorTime; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    { threadError = "Thread measurement incomplete: " + ex.Message; }
                }
            }
            var elapsed = _lastTimestamp.HasValue
                ? Stopwatch.GetElapsedTime(_lastTimestamp.Value, timestamp).TotalSeconds : (double?)null;
            double? coreCpu = elapsed is > 0 ? 100 * (cpu - _lastCpu).TotalSeconds / elapsed : null;
            double? busiest = null;
            if (elapsed is > 0 && threadError is null)
            {
                var deltas = threads.Where(t => _lastThreadCpu.ContainsKey(t.Key))
                    .Select(t => 100 * (t.Value - _lastThreadCpu[t.Key]).TotalSeconds / elapsed.Value)
                    .Where(value => value >= 0).ToArray();
                if (deltas.Length > 0) busiest = deltas.Max();
            }
            _lastTimestamp = timestamp;
            _lastCpu = cpu;
            _lastThreadCpu = threads;
            return new LoadProcessSample(at, _pid, _startUtc, elapsed,
                coreCpu, busiest, process.PrivateMemorySize64, process.WorkingSet64,
                process.VirtualMemorySize64, process.HandleCount, process.Threads.Count,
                threadError);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            // Never bridge a missing interval and present it as a normal point sample.
            _lastTimestamp = null;
            _lastThreadCpu.Clear();
            return new LoadProcessSample(at, _pid, _startUtc, null, null, null,
                null, null, null, null, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static double? Percentile(IEnumerable<double?> values, double percentile)
    {
        if (percentile is <= 0 or > 1 || double.IsNaN(percentile))
            throw new ArgumentOutOfRangeException(nameof(percentile));
        var sorted = values.Where(v => v.HasValue && double.IsFinite(v.Value))
            .Select(v => v!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    }

    public static void RunSelfTest()
    {
        if (Percentile(Array.Empty<double?>(), .95) is not null
            || Percentile(new double?[] { null, 1, 2, 3, 100 }, .95) != 100
            || Percentile(new double?[] { 3, 1, 2 }, .5) != 2)
            throw new InvalidDataException("Resource percentiles must preserve missing data and nearest-rank tails.");
        var sampler = new LoadProcessSampler(Environment.ProcessId);
        var first = sampler.Read();
        if (first.ProcessCoreCpuPercent is not null || first.PrivateBytes is not > 0 || first.Error is not null)
            throw new InvalidDataException("First sample must measure memory without inventing a CPU interval: " + first.Error);
        var second = sampler.Read();
        if (second.IntervalSeconds is not > 0 || second.ProcessCoreCpuPercent is not >= 0 || second.Error is not null)
            throw new InvalidDataException("Live process CPU interval is missing: " + second.Error);
    }
}

public sealed record LoadProcessSample(
    DateTimeOffset AtUtc, int ProcessId, DateTime ProcessStartedUtc, double? IntervalSeconds,
    double? ProcessCoreCpuPercent, double? BusiestObservedThreadCpuPercent,
    long? PrivateBytes, long? WorkingSetBytes, long? VirtualBytes, int? Handles, int? Threads,
    string? Error);
