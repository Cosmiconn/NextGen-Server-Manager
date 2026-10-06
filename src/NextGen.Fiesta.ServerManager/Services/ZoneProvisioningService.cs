using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed record ZoneProvisionResult(bool Success, string Summary, string BackupDirectory, FiestaServiceEntry? CreatedService = null);

public sealed class ZoneProvisioningService
{
    private readonly ServerInfoParser _parser = new();
    private readonly WindowsServiceManager _serviceManager;
    private readonly ZoneLifecycleService _lifecycle;
    private readonly WindowsFirewallService _firewall;

    private static readonly Regex MyServerRx = new(
        "MY_SERVER\\s+\"[^\"]+\"\\s*,\\s*\"[^\"]+\"\\s*,\\s*6\\s*,\\s*0\\s*,\\s*\\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ZoneProvisioningService(WindowsServiceManager serviceManager, ZoneLifecycleService lifecycle, WindowsFirewallService firewall)
    {
        _serviceManager = serviceManager;
        _lifecycle = lifecycle;
        _firewall = firewall;
    }

    public async Task<ZoneProvisionPlan> CreatePlanAsync(string serverRoot, IReadOnlyList<FiestaServiceEntry> services, CancellationToken ct = default)
    {
        var zones = services.Where(x => x.Kind == FiestaServiceKind.Zone && x.ZoneNumber.HasValue).OrderBy(x => x.ZoneNumber).ToList();
        var serverInfo = Path.Combine(serverRoot, "9Data", "ServerInfo", "ServerInfo.txt");
        var parsedInfo = _parser.Parse(serverInfo);
        var knownZoneIds = zones.Select(x => x.ZoneNumber!.Value)
            .Concat(parsedInfo.Where(x => x.ServerType == 6).Select(x => x.ZoneNo))
            .Distinct()
            .ToArray();
        var next = knownZoneIds.Length == 0 ? 0 : knownZoneIds.Max() + 1;
        var source = zones.FirstOrDefault(x => File.Exists(x.ExecutablePath) && x.ConfigPath is not null && File.Exists(x.ConfigPath));
        var sourceNo = source?.ZoneNumber ?? 0;
        var target = Path.Combine(serverRoot, $"Zone{next:00}");
        var serviceName = $"_Zone{next}";

        var configuredPorts = parsedInfo.Select(x => x.Port).ToArray();
        var used = new HashSet<int>(configuredPorts);
        foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) used.Add(listener.Port);
        // Start after the highest configured Fiesta port, not after unrelated high ephemeral/listener ports on Windows.
        var start = Math.Max(9035, configuredPorts.Length == 0 ? 9035 : configuredPorts.Max() + 1);
        while (start <= 65533 && (used.Contains(start) || used.Contains(start + 1) || used.Contains(start + 2))) start++;
        var portsAvailable = start <= 65533;
        var serviceState = await _serviceManager.QueryAsync(serviceName, ct);

        var detail = source is null
            ? "Keine gültige Quellzone mit Zone.exe und ZoneServerInfo.txt gefunden."
            : portsAvailable
                ? "Quellzone wird ohne Logdateien geklont. ServerInfo wird gesichert; nur der neue Client-Port erhält eine eingehende Firewallregel. Interner und OPTool-Port werden nicht extern geöffnet."
                : "Kein freier Block aus drei aufeinanderfolgenden TCP-Ports gefunden.";

        return new ZoneProvisionPlan
        {
            ZoneNumber = next,
            SourceZoneNumber = sourceNo,
            SourceDirectory = source?.DirectoryPath ?? string.Empty,
            TargetDirectory = target,
            ClientPort = portsAvailable ? start : 0,
            InternalPort = portsAvailable ? start + 1 : 0,
            OptoolPort = portsAvailable ? start + 2 : 0,
            FirewallRuleName = $"NextGen Fiesta Zone{next:00} Client TCP {start}",
            PortsAvailable = portsAvailable,
            TargetAvailable = !Directory.Exists(target),
            ServiceAvailable = serviceState.State == ServiceRuntimeState.Missing,
            Detail = detail
        };
    }

    public async Task<ZoneProvisionResult> ProvisionAsync(string serverRoot, ZoneProvisionPlan plan, bool configureRecovery, CancellationToken ct = default)
    {
        if (!plan.IsValid) return new ZoneProvisionResult(false, "Provisionierungsplan ist nicht gültig.", string.Empty);

        var serverInfo = Path.Combine(serverRoot, "9Data", "ServerInfo", "ServerInfo.txt");
        if (!File.Exists(serverInfo)) return new ZoneProvisionResult(false, $"ServerInfo fehlt: {serverInfo}", string.Empty);

        // Revalidate immediately before writing; the plan may be several minutes old.
        if (Directory.Exists(plan.TargetDirectory))
            return new ZoneProvisionResult(false, $"Zielordner existiert inzwischen: {plan.TargetDirectory}", string.Empty);
        if ((await _serviceManager.QueryAsync($"_Zone{plan.ZoneNumber}", ct)).State != ServiceRuntimeState.Missing)
            return new ZoneProvisionResult(false, $"Windows-Dienst _Zone{plan.ZoneNumber} existiert inzwischen.", string.Empty);
        var currentInfo = _parser.Parse(serverInfo);
        if (currentInfo.Any(x => x.ServerType == 6 && x.ZoneNo == plan.ZoneNumber))
            return new ZoneProvisionResult(false, $"ServerInfo enthält Zone {plan.ZoneNumber} inzwischen bereits.", string.Empty);
        var currentPorts = new HashSet<int>(currentInfo.Select(x => x.Port));
        foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) currentPorts.Add(listener.Port);
        if (new[] { plan.ClientPort, plan.InternalPort, plan.OptoolPort }.Any(currentPorts.Contains))
            return new ZoneProvisionResult(false, "Mindestens einer der geplanten Ports ist inzwischen belegt. Bitte den Zone-Plan neu erzeugen.", string.Empty);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(serverRoot, ".nextgen-backups", "provisioning", $"Zone{plan.ZoneNumber:00}-{stamp}");
        Directory.CreateDirectory(backupDir);
        var serverInfoBackup = Path.Combine(backupDir, "ServerInfo.txt");
        File.Copy(serverInfo, serverInfoBackup, overwrite: true);

        var targetCreated = false;
        var serviceInstalled = false;
        var firewallTouched = false;
        FiestaServiceEntry? created = null;
        try
        {
            CopyZoneTemplate(plan.SourceDirectory, plan.TargetDirectory);
            targetCreated = true;

            var config = Path.Combine(plan.TargetDirectory, "ZoneServerInfo", "ZoneServerInfo.txt");
            RewriteZoneServerInfo(config, plan.ZoneNumber);
            AddServerInfoRows(serverInfo, plan);

            created = new FiestaServiceEntry
            {
                DisplayName = $"Zone{plan.ZoneNumber:00}",
                ServiceName = $"_Zone{plan.ZoneNumber}",
                Kind = FiestaServiceKind.Zone,
                ZoneNumber = plan.ZoneNumber,
                DirectoryPath = plan.TargetDirectory,
                ExecutablePath = Path.Combine(plan.TargetDirectory, "Zone.exe"),
                ConfigPath = config,
                PdbPath = Directory.EnumerateFiles(plan.TargetDirectory, "*.pdb", SearchOption.TopDirectoryOnly).FirstOrDefault(),
                ClientPort = plan.ClientPort,
                InternalPort = plan.InternalPort,
                OptoolPort = plan.OptoolPort
            };

            var install = await _lifecycle.InstallNativeAsync(created, ct);
            if (!install.Success) throw new InvalidOperationException("Dienstinstallation fehlgeschlagen: " + (install.StdErr + install.StdOut).Trim());
            serviceInstalled = true;

            if (configureRecovery)
            {
                var recovery = await _serviceManager.ConfigureRecoveryAsync(created.ServiceName, ct);
                if (!recovery.Success) throw new InvalidOperationException("Recovery-Konfiguration fehlgeschlagen: " + (recovery.StdErr + recovery.StdOut).Trim());
            }

            var firewallExisted = await _firewall.RuleExistsAsync(plan.FirewallRuleName, ct);
            var fw = await _firewall.EnsureInboundTcpPortAsync(plan.FirewallRuleName, plan.ClientPort, ct);
            if (!fw.Success) throw new InvalidOperationException("Firewallregel fehlgeschlagen: " + (fw.StdErr + fw.StdOut).Trim());
            firewallTouched = !firewallExisted;

            return new ZoneProvisionResult(
                true,
                $"Zone{plan.ZoneNumber:00} angelegt und als {created.ServiceName} registriert. Ports {plan.ClientPort}/{plan.InternalPort}/{plan.OptoolPort}. Firewall: TCP {plan.ClientPort} eingehend. Die Zone wurde absichtlich NICHT gestartet und noch keinen Maps zugewiesen.",
                backupDir,
                created);
        }
        catch (Exception ex)
        {
            // Best-effort transaction rollback. Only resources created by this operation are touched.
            try { if (firewallTouched) await _firewall.DeleteRuleAsync(plan.FirewallRuleName, ct); } catch { }
            try
            {
                if (created is not null && (serviceInstalled || (await _serviceManager.QueryAsync(created.ServiceName, ct)).State != ServiceRuntimeState.Missing))
                    await _lifecycle.UninstallServiceAsync(created, ct);
            }
            catch { }
            try { File.Copy(serverInfoBackup, serverInfo, overwrite: true); } catch { }
            try { if (targetCreated && Directory.Exists(plan.TargetDirectory)) Directory.Delete(plan.TargetDirectory, recursive: true); } catch { }
            return new ZoneProvisionResult(false, "Provisionierung zurückgerollt: " + ex.Message, backupDir);
        }
    }

    private static void CopyZoneTemplate(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            if (ShouldSkip(rel, isDirectory: true)) continue;
            Directory.CreateDirectory(Path.Combine(target, rel));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (ShouldSkip(rel, isDirectory: false)) continue;
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: false);
        }
    }

    private static bool ShouldSkip(string relativePath, bool isDirectory)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(x => x.Equals("logs", StringComparison.OrdinalIgnoreCase)
                        || x.Equals("log", StringComparison.OrdinalIgnoreCase)
                        || x.Equals("debugmessage", StringComparison.OrdinalIgnoreCase)
                        || x.Equals(".nextgen-cache", StringComparison.OrdinalIgnoreCase))) return true;
        if (isDirectory) return false;
        var ext = Path.GetExtension(relativePath);
        if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".err", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".out", StringComparison.OrdinalIgnoreCase) || ext.Equals(".trace", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dbg", StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(relativePath);
        return name.Contains("Message", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Debug", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Crash", StringComparison.OrdinalIgnoreCase);
    }

    private static void RewriteZoneServerInfo(string config, int zone)
    {
        if (!File.Exists(config)) throw new FileNotFoundException("ZoneServerInfo.txt fehlt im Template.", config);
        var text = File.ReadAllText(config, Encoding.Latin1);
        var replacement = $"MY_SERVER \"_Zone{zone}\",\t\t\"_Zone{zone}\",\t6,\t\t0,\t\t{zone}";
        if (!MyServerRx.IsMatch(text)) throw new InvalidDataException("MY_SERVER konnte in der geklonten ZoneServerInfo.txt nicht gefunden werden.");
        File.WriteAllText(config, MyServerRx.Replace(text, replacement, 1), Encoding.Latin1);
    }

    private void AddServerInfoRows(string serverInfoPath, ZoneProvisionPlan plan)
    {
        var entries = _parser.Parse(serverInfoPath);
        var sourceRows = entries.Where(x => x.ServerType == 6 && x.ZoneNo == plan.SourceZoneNumber && new[] { 20, 6, 8 }.Contains(x.ConnectionKind)).ToList();
        if (sourceRows.Count < 3) throw new InvalidDataException($"ServerInfo enthält für Zone{plan.SourceZoneNumber:00} nicht alle drei Referenz-Endpunkte (20/6/8).");
        if (entries.Any(x => x.ServerType == 6 && x.ZoneNo == plan.ZoneNumber)) throw new InvalidOperationException($"ServerInfo enthält Zone {plan.ZoneNumber} bereits.");

        string Row(int kind, int port)
        {
            var src = sourceRows.First(x => x.ConnectionKind == kind);
            var comment = kind == 20 ? "PUBLIC_IP" : "LOCALHOST";
            return $"SERVER_INFO  \"PG_W00_Z{plan.ZoneNumber:00}\",   6, 0, {plan.ZoneNumber}, {kind},  \"{src.Host}\",\t\t{port},  {src.BackLog},  {src.MaxAccept}  ; \t{comment}";
        }

        var lines = File.ReadAllLines(serverInfoPath, Encoding.Latin1).ToList();
        var insertAfter = -1;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Contains("SERVER_INFO", StringComparison.OrdinalIgnoreCase) && lines[i].Contains("PG_W00_Z", StringComparison.OrdinalIgnoreCase)) insertAfter = i;
        if (insertAfter < 0) throw new InvalidDataException("Kein Zone-SERVER_INFO-Block gefunden.");

        lines.InsertRange(insertAfter + 1, new[]
        {
            string.Empty,
            Row(20, plan.ClientPort),
            Row(6, plan.InternalPort),
            Row(8, plan.OptoolPort)
        });
        File.WriteAllLines(serverInfoPath, lines, Encoding.Latin1);
    }
}
