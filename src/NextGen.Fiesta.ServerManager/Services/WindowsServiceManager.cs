using System.Diagnostics;
using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class WindowsServiceManager
{
    private readonly CommandRunner _runner;
    private static readonly Regex StateRx = new(@"STATE\s*:\s*(?<num>\d+)\s+(?<name>\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PidRx = new(@"PID\s*:\s*(?<pid>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public WindowsServiceManager(CommandRunner runner) => _runner = runner;

    public async Task<(ServiceRuntimeState State, int? Pid)> QueryAsync(string serviceName, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync("sc.exe", $"queryex \"{serviceName}\"", timeoutMs: 8000, cancellationToken: ct);
        var all = result.StdOut + "\n" + result.StdErr;
        if (all.Contains("1060", StringComparison.OrdinalIgnoreCase) || all.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return (ServiceRuntimeState.Missing, null);

        var sm = StateRx.Match(all);
        var pm = PidRx.Match(all);
        int? pid = pm.Success && int.TryParse(pm.Groups["pid"].Value, out var p) && p > 0 ? p : null;
        if (!sm.Success) return (ServiceRuntimeState.Unknown, pid);
        var stateNumber = int.TryParse(sm.Groups["num"].Value, out var n) ? n : -1;
        return (stateNumber switch
        {
            1 => ServiceRuntimeState.Stopped,
            2 => ServiceRuntimeState.StartPending,
            3 => ServiceRuntimeState.StopPending,
            4 => ServiceRuntimeState.Running,
            7 => ServiceRuntimeState.Paused,
            _ => ServiceRuntimeState.Unknown
        }, pid);
    }

    public Task<CommandResult> StartAsync(string serviceName, CancellationToken ct = default) =>
        _runner.RunAsync("sc.exe", $"start \"{serviceName}\"", timeoutMs: 20000, cancellationToken: ct);

    public Task<CommandResult> StopAsync(string serviceName, CancellationToken ct = default) =>
        _runner.RunAsync("sc.exe", $"stop \"{serviceName}\"", timeoutMs: 20000, cancellationToken: ct);

    public async Task<CommandResult> RestartAsync(string serviceName, CancellationToken ct = default)
    {
        var state = await QueryAsync(serviceName, ct);
        if (state.State is ServiceRuntimeState.Running or ServiceRuntimeState.StartPending)
        {
            await StopAsync(serviceName, ct);
            await WaitForStateAsync(serviceName, ServiceRuntimeState.Stopped, TimeSpan.FromSeconds(20), ct);
        }
        return await StartAsync(serviceName, ct);
    }

    public Task<CommandResult> DeleteAsync(string serviceName, CancellationToken ct = default) =>
        _runner.RunAsync("sc.exe", $"delete \"{serviceName}\"", timeoutMs: 10000, cancellationToken: ct);

    public Task<CommandResult> ConfigureRecoveryAsync(string serviceName, CancellationToken ct = default) =>
        _runner.RunAsync("sc.exe", $"failure \"{serviceName}\" reset= 86400 actions= restart/5000/restart/10000/restart/30000", timeoutMs: 10000, cancellationToken: ct);

    public async Task<bool> WaitForStateAsync(string serviceName, ServiceRuntimeState target, TimeSpan timeout, CancellationToken ct = default)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var state = await QueryAsync(serviceName, ct);
            if (state.State == target) return true;
            await Task.Delay(500, ct);
        }
        return false;
    }

    public DateTime? GetProcessStartTime(int? pid)
    {
        if (pid is null or <= 0) return null;
        try { return Process.GetProcessById(pid.Value).StartTime; } catch { return null; }
    }
}
