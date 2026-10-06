using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed record OrchestrationEvent(DateTime Timestamp, string Message, DiagnosticSeverity Severity = DiagnosticSeverity.Info);

public sealed class SmartOrchestrator
{
    private readonly WindowsServiceManager _services;
    private readonly NetworkInspector _network;
    private readonly HealthEngine _health;
    private readonly LogDiscoveryService _logs = new();
    public event Action<OrchestrationEvent>? Progress;

    public SmartOrchestrator(WindowsServiceManager services, NetworkInspector network, HealthEngine health)
    {
        _services = services;
        _network = network;
        _health = health;
    }

    public async Task SmartStartAsync(IReadOnlyList<FiestaServiceEntry> all, AppSettings settings, CancellationToken ct = default)
    {
        var coreOrder = new[]
        {
            FiestaServiceKind.Account,
            FiestaServiceKind.AccountLog,
            FiestaServiceKind.Login,
            FiestaServiceKind.Character,
            FiestaServiceKind.GameLog,
            FiestaServiceKind.WorldManager,
            FiestaServiceKind.GamigoZR
        };

        foreach (var kind in coreOrder)
        {
            var svc = all.FirstOrDefault(x => x.Kind == kind);
            if (svc is null) continue;
            await EnsureStartedAsync(svc, ct);
            if (kind != FiestaServiceKind.WorldManager) await Task.Delay(400, ct);
        }

        var wm = all.FirstOrDefault(x => x.Kind == FiestaServiceKind.WorldManager);
        if (wm is null) throw new InvalidOperationException("WorldManager wurde nicht gefunden.");
        var wmState = await _services.QueryAsync(wm.ServiceName, ct);
        var wmStarted = _services.GetProcessStartTime(wmState.Pid) ?? DateTime.Now;
        var wmPort = wm.InternalPort ?? 9014;

        Report($"Warte auf WorldManager Zone-Listener TCP {wmPort} …");
        if (!await WaitPortAsync(wmPort, TimeSpan.FromSeconds(settings.WorldManagerReadyTimeoutSeconds), ct))
            throw new InvalidOperationException($"WorldManager wurde innerhalb von {settings.WorldManagerReadyTimeoutSeconds}s nicht auf Port {wmPort} bereit.");
        Report($"WorldManager Port {wmPort} ist offen; prüfe vollständige Initialisierung …");

        // On NA2016 the listener may be open before the DB/Guild startup sequence has settled.
        // Prefer the actual RUNNING log marker from the current process. If no marker is available,
        // use a conservative stability delay rather than immediately launching every zone.
        var markerWait = Math.Min(30, Math.Max(8, settings.WorldManagerReadyTimeoutSeconds / 3));
        var markerSeen = await WaitForLogMarkerAsync(wm.DirectoryPath, "SUCCESSED RUNNING SERVER", wmStarted, TimeSpan.FromSeconds(markerWait), ct);
        if (markerSeen)
            Report("WorldManager meldet SUCCESSED RUNNING SERVER – Zonen dürfen starten.");
        else
        {
            Report($"Kein aktueller WM-Ready-Marker gefunden; verwende {Math.Max(8, settings.ZoneStartGapSeconds + 6)}s Stabilitätsfenster.", DiagnosticSeverity.Warning);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(8, settings.ZoneStartGapSeconds + 6)), ct);
            var check = await _services.QueryAsync(wm.ServiceName, ct);
            if (check.State != ServiceRuntimeState.Running || !await _network.IsPortOpenAsync(wmPort, ct: ct))
                throw new InvalidOperationException("WorldManager verlor während des Readiness-Fensters seinen RUNNING-/Port-Zustand.");
        }

        foreach (var zone in all.Where(x => x.Kind == FiestaServiceKind.Zone).OrderBy(x => x.ZoneNumber))
        {
            ct.ThrowIfCancellationRequested();
            Report($"Starte {zone.DisplayName} …");
            await EnsureStartedAsync(zone, ct);

            var ready = await WaitZoneStableAsync(zone, settings, ct);
            if (!ready)
            {
                var state = await _services.QueryAsync(zone.ServiceName, ct);
                Report($"{zone.DisplayName} nicht stabil: Dienst={state.State}, Client-Port {zone.ClientPort?.ToString() ?? "-"}.", DiagnosticSeverity.Error);
                if (state.State == ServiceRuntimeState.Stopped)
                {
                    Report($"Einmaliger Recovery-Neustart für {zone.DisplayName} …", DiagnosticSeverity.Warning);
                    await _services.StartAsync(zone.ServiceName, ct);
                    ready = await WaitZoneStableAsync(zone, settings, ct);
                }
                if (!ready) throw new InvalidOperationException($"{zone.DisplayName} konnte nicht stabil gestartet werden.");
            }

            Report(zone.ClientPort is int port
                ? $"{zone.DisplayName} ist stabil und auf TCP {port} bereit."
                : $"{zone.DisplayName} ist stabil im Zustand RUNNING.");

            if (settings.UseServiceRecovery)
                await _services.ConfigureRecoveryAsync(zone.ServiceName, ct);
            await Task.Delay(TimeSpan.FromSeconds(settings.ZoneStartGapSeconds), ct);
        }
        Report("Smart Start abgeschlossen.");
    }

    public async Task SmartStopAsync(IReadOnlyList<FiestaServiceEntry> all, CancellationToken ct = default)
    {
        foreach (var zone in all.Where(x => x.Kind == FiestaServiceKind.Zone).OrderByDescending(x => x.ZoneNumber))
            await StopIfRunning(zone, ct);

        var reverseCore = new[]
        {
            FiestaServiceKind.GamigoZR,
            FiestaServiceKind.WorldManager,
            FiestaServiceKind.GameLog,
            FiestaServiceKind.Character,
            FiestaServiceKind.Login,
            FiestaServiceKind.AccountLog,
            FiestaServiceKind.Account
        };
        foreach (var kind in reverseCore)
        {
            var svc = all.FirstOrDefault(x => x.Kind == kind);
            if (svc is not null) await StopIfRunning(svc, ct);
        }
        Report("Smart Stop abgeschlossen.");
    }

    public async Task SmartRestartAsync(IReadOnlyList<FiestaServiceEntry> all, AppSettings settings, CancellationToken ct = default)
    {
        await SmartStopAsync(all, ct);
        await Task.Delay(2000, ct);
        await SmartStartAsync(all, settings, ct);
    }

    private async Task EnsureStartedAsync(FiestaServiceEntry svc, CancellationToken ct)
    {
        var state = await _services.QueryAsync(svc.ServiceName, ct);
        if (state.State == ServiceRuntimeState.Missing)
            throw new InvalidOperationException($"Dienst {svc.ServiceName} fehlt.");
        if (state.State == ServiceRuntimeState.Running)
        {
            Report($"{svc.DisplayName} läuft bereits.");
            return;
        }
        var start = await _services.StartAsync(svc.ServiceName, ct);
        if (!start.Success && !start.StdOut.Contains("already", StringComparison.OrdinalIgnoreCase))
            Report($"Startmeldung {svc.ServiceName}: {start.StdOut} {start.StdErr}".Trim(), DiagnosticSeverity.Warning);
        if (!await _services.WaitForStateAsync(svc.ServiceName, ServiceRuntimeState.Running, TimeSpan.FromSeconds(25), ct))
            throw new InvalidOperationException($"{svc.DisplayName} erreicht den Zustand RUNNING nicht.");
    }

    private async Task<bool> WaitZoneStableAsync(FiestaServiceEntry zone, AppSettings settings, CancellationToken ct)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(settings.ZoneReadyTimeoutSeconds);
        DateTime? stableSince = null;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            var state = await _services.QueryAsync(zone.ServiceName, ct);
            var portReady = zone.ClientPort is not int port || await _network.IsPortOpenAsync(port, ct: ct);
            if (state.State == ServiceRuntimeState.Stopped || state.State == ServiceRuntimeState.Missing) return false;

            if (state.State == ServiceRuntimeState.Running && portReady)
            {
                stableSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(3)) return true;
            }
            else stableSince = null;
            await Task.Delay(700, ct);
        }
        return false;
    }

    private async Task StopIfRunning(FiestaServiceEntry svc, CancellationToken ct)
    {
        var state = await _services.QueryAsync(svc.ServiceName, ct);
        if (state.State != ServiceRuntimeState.Running) return;
        Report($"Stoppe {svc.DisplayName} …");
        await _services.StopAsync(svc.ServiceName, ct);
        await _services.WaitForStateAsync(svc.ServiceName, ServiceRuntimeState.Stopped, TimeSpan.FromSeconds(25), ct);
    }

    private async Task<bool> WaitPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            if (await _network.IsPortOpenAsync(port, ct: ct)) return true;
            await Task.Delay(700, ct);
        }
        return false;
    }

    private async Task<bool> WaitForLogMarkerAsync(string directory, string marker, DateTime processStart, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            foreach (var path in _logs.FindLogs(directory).Take(6))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < processStart.AddSeconds(-2)) continue;
                    foreach (var line in _logs.Tail(path, 500).Reverse())
                    {
                        if (!line.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
                        var timestamp = ParseTimestamp(line);
                        if (timestamp is null || timestamp >= processStart.AddSeconds(-2)) return true;
                    }
                }
                catch { }
            }
            await Task.Delay(750, ct);
        }
        return false;
    }

    private static DateTime? ParseTimestamp(string line)
    {
        var match = Regex.Match(line, @"(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})");
        return match.Success && DateTime.TryParse($"{match.Groups["date"].Value} {match.Groups["time"].Value}", out var value) ? value : null;
    }

    private void Report(string message, DiagnosticSeverity severity = DiagnosticSeverity.Info) => Progress?.Invoke(new OrchestrationEvent(DateTime.Now, message, severity));
}
