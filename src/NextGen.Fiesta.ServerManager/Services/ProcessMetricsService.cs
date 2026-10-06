using System.Diagnostics;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed record ProcessMetrics(
    double CpuPercent,
    double CpuCorePercent,
    double WorkingSetMb,
    double PrivateMemoryMb,
    double VirtualMemoryMb,
    int HandleCount,
    int ThreadCount);

public sealed class ProcessMetricsService
{
    private readonly Dictionary<int, Sample> _samples = new();
    private readonly object _gate = new();

    private sealed record Sample(TimeSpan Cpu, DateTime Timestamp);

    public ProcessMetrics? SampleProcess(int? pid)
    {
        if (pid is null or <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            process.Refresh();
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            double totalMachinePercent = 0;
            double coreEquivalentPercent = 0;

            lock (_gate)
            {
                if (_samples.TryGetValue(pid.Value, out var previous))
                {
                    var wall = (now - previous.Timestamp).TotalMilliseconds;
                    var cpuMs = (cpu - previous.Cpu).TotalMilliseconds;
                    if (wall > 0)
                    {
                        coreEquivalentPercent = Math.Max(0, cpuMs / wall * 100.0);
                        totalMachinePercent = Math.Max(0, coreEquivalentPercent / Math.Max(1, Environment.ProcessorCount));
                    }
                }
                _samples[pid.Value] = new Sample(cpu, now);

                if (_samples.Count > 128)
                {
                    foreach (var stale in _samples.Keys.Take(_samples.Count - 96).ToList()) _samples.Remove(stale);
                }
            }

            return new ProcessMetrics(
                totalMachinePercent,
                coreEquivalentPercent,
                process.WorkingSet64 / 1024d / 1024d,
                process.PrivateMemorySize64 / 1024d / 1024d,
                process.VirtualMemorySize64 / 1024d / 1024d,
                process.HandleCount,
                process.Threads.Count);
        }
        catch
        {
            lock (_gate) _samples.Remove(pid.Value);
            return null;
        }
    }
}
