using System.Diagnostics;
using System.IO.Compression;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class ZoneLifecycleService
{
    private readonly WindowsServiceManager _services;
    private readonly CommandRunner _runner;

    public ZoneLifecycleService(WindowsServiceManager services, CommandRunner runner)
    {
        _services = services;
        _runner = runner;
    }

    public async Task<CommandResult> InstallNativeAsync(FiestaServiceEntry service, CancellationToken ct = default)
    {
        if (!File.Exists(service.ExecutablePath))
            return new CommandResult(-1, string.Empty, $"Executable fehlt: {service.ExecutablePath}");

        var q = await _services.QueryAsync(service.ServiceName, ct);
        if (q.State != ServiceRuntimeState.Missing)
            return new CommandResult(0, $"{service.ServiceName} ist bereits installiert.", string.Empty);

        // Stock NA2016 installs core/zone services by starting each service executable once.
        // GamigoZR is the exception: the stock script registers it explicitly with sc.exe create.
        if (service.Kind == FiestaServiceKind.GamigoZR)
        {
            var create = await _runner.RunAsync("sc.exe", $"create \"{service.ServiceName}\" binpath= \"{service.ExecutablePath}\"", timeoutMs: 10000, cancellationToken: ct);
            if (!create.Success) return create;
        }
        else
        {
            var psi = new ProcessStartInfo(service.ExecutablePath)
            {
                WorkingDirectory = service.DirectoryPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is not null)
            {
                try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(12), ct); }
                catch { /* Some Fiesta service binaries may stay alive after registration. */ }
            }
        }

        for (var i = 0; i < 20; i++)
        {
            var state = await _services.QueryAsync(service.ServiceName, ct);
            if (state.State != ServiceRuntimeState.Missing)
                return new CommandResult(0, $"{service.ServiceName} wurde registriert.", string.Empty);
            await Task.Delay(500, ct);
        }
        return new CommandResult(-1, string.Empty, $"Der native NA2016-Installer hat {service.ServiceName} nicht registriert.");
    }

    public async Task<CommandResult> ReinstallAsync(FiestaServiceEntry service, bool configureRecovery = true, CancellationToken ct = default)
    {
        var current = await _services.QueryAsync(service.ServiceName, ct);
        if (current.State is ServiceRuntimeState.Running or ServiceRuntimeState.StartPending)
        {
            await _services.StopAsync(service.ServiceName, ct);
            await _services.WaitForStateAsync(service.ServiceName, ServiceRuntimeState.Stopped, TimeSpan.FromSeconds(20), ct);
        }
        if (current.State != ServiceRuntimeState.Missing)
        {
            await _services.DeleteAsync(service.ServiceName, ct);
            for (var i = 0; i < 20; i++)
            {
                if ((await _services.QueryAsync(service.ServiceName, ct)).State == ServiceRuntimeState.Missing) break;
                await Task.Delay(500, ct);
            }
        }

        var install = await InstallNativeAsync(service, ct);
        if (!install.Success) return install;
        if (configureRecovery) await _services.ConfigureRecoveryAsync(service.ServiceName, ct);
        return install;
    }

    public string CreateZoneBackup(FiestaServiceEntry zone, string serverRoot)
    {
        if (zone.Kind != FiestaServiceKind.Zone) throw new InvalidOperationException("Nur Zonen können als Zone-Backup gesichert werden.");
        var backupDir = Path.Combine(serverRoot, ".nextgen-backups", "zones");
        Directory.CreateDirectory(backupDir);
        var zip = Path.Combine(backupDir, $"{zone.DisplayName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        ZipFile.CreateFromDirectory(zone.DirectoryPath, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }

    public async Task<CommandResult> UninstallServiceAsync(FiestaServiceEntry service, CancellationToken ct = default)
    {
        var state = await _services.QueryAsync(service.ServiceName, ct);
        if (state.State == ServiceRuntimeState.Running)
        {
            await _services.StopAsync(service.ServiceName, ct);
            await _services.WaitForStateAsync(service.ServiceName, ServiceRuntimeState.Stopped, TimeSpan.FromSeconds(20), ct);
        }
        return await _services.DeleteAsync(service.ServiceName, ct);
    }
}
