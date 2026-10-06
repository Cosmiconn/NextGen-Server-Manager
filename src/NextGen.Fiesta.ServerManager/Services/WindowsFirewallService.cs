namespace NextGen.Fiesta.ServerManager.Services;

public sealed class WindowsFirewallService
{
    private readonly CommandRunner _runner;
    public WindowsFirewallService(CommandRunner runner) => _runner = runner;

    public async Task<bool> RuleExistsAsync(string ruleName, CancellationToken ct = default)
    {
        var existing = await _runner.RunAsync("netsh.exe", $"advfirewall firewall show rule name=\"{ruleName}\"", timeoutMs: 10000, cancellationToken: ct);
        var text = existing.StdOut + existing.StdErr;
        if (!existing.Success) return false;
        return !text.Contains("No rules match", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Keine Regeln", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CommandResult> EnsureInboundTcpPortAsync(string ruleName, int port, CancellationToken ct = default)
    {
        if (await RuleExistsAsync(ruleName, ct))
            return new CommandResult(0, $"Firewallregel '{ruleName}' existiert bereits.", string.Empty);

        var add = await _runner.RunAsync(
            "netsh.exe",
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port} profile=any enable=yes",
            timeoutMs: 15000,
            cancellationToken: ct);
        if (!add.Success) return add;
        return await RuleExistsAsync(ruleName, ct)
            ? add
            : new CommandResult(-1, add.StdOut, "Firewallregel wurde von netsh nicht verifiziert.");
    }

    public Task<CommandResult> DeleteRuleAsync(string ruleName, CancellationToken ct = default) =>
        _runner.RunAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{ruleName}\"", timeoutMs: 10000, cancellationToken: ct);
}
