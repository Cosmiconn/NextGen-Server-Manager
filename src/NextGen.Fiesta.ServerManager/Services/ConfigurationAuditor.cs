using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class ConfigurationAuditor
{
    private static readonly Regex MyServerRx = new(
        "MY_SERVER\\s+\"(?<a>[^\"]+)\"\\s*,\\s*\"(?<b>[^\"]+)\"\\s*,\\s*(?<type>-?\\d+)\\s*,\\s*(?<world>-?\\d+)\\s*,\\s*(?<zone>-?\\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<DiagnosticIssue> Audit(string serverRoot, IReadOnlyList<FiestaServiceEntry> services)
    {
        var issues = new List<DiagnosticIssue>();
        var portOwners = new Dictionary<int, List<string>>();

        foreach (var svc in services)
        {
            foreach (var port in new[] { svc.ClientPort, svc.InternalPort, svc.OptoolPort }.Where(x => x.HasValue).Select(x => x!.Value))
            {
                if (!portOwners.TryGetValue(port, out var owners)) portOwners[port] = owners = new List<string>();
                owners.Add(svc.DisplayName);
            }

            if (svc.Kind == FiestaServiceKind.Zone && svc.ZoneNumber is int zone)
            {
                if (!File.Exists(svc.ExecutablePath))
                    issues.Add(Issue("NG-ZONE-0003", DiagnosticSeverity.Critical, "Zone.exe fehlt", $"{svc.DisplayName} besitzt keine Zone.exe.", "Zone aus einer geprüften Referenz wiederherstellen oder über 'Neu installieren' rekonstruieren.", svc, 1.0, RepairActionKind.ReinstallService));

                if (string.IsNullOrWhiteSpace(svc.ConfigPath) || !File.Exists(svc.ConfigPath))
                {
                    issues.Add(Issue("NG-ZONE-0005", DiagnosticSeverity.Error, "ZoneServerInfo fehlt", $"{svc.DisplayName} besitzt keine ZoneServerInfo.txt.", "ZoneServerInfo aus einer Referenzzone erzeugen und Zone-ID prüfen.", svc, 1.0, RepairActionKind.FixZoneServerInfo));
                }
                else
                {
                    var text = File.ReadAllText(svc.ConfigPath);
                    var m = MyServerRx.Match(text);
                    if (!m.Success)
                    {
                        issues.Add(Issue("NG-ZONE-0005", DiagnosticSeverity.Error, "MY_SERVER nicht lesbar", $"MY_SERVER konnte in {svc.ConfigPath} nicht interpretiert werden.", "ZoneServerInfo-Struktur prüfen.", svc, 0.95, RepairActionKind.FixZoneServerInfo));
                    }
                    else
                    {
                        var configuredZone = int.Parse(m.Groups["zone"].Value);
                        var a = m.Groups["a"].Value;
                        var b = m.Groups["b"].Value;
                        if (configuredZone != zone || !a.Equals($"_Zone{zone}", StringComparison.OrdinalIgnoreCase) || !b.Equals($"_Zone{zone}", StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add(Issue("NG-ZONE-0005", DiagnosticSeverity.Error, "Zone-ID stimmt nicht", $"Ordner {svc.DisplayName} erwartet Zone {zone}, ZoneServerInfo meldet aber '{a}', '{b}', Zone {configuredZone}.", "MY_SERVER auf die tatsächliche Zone-ID korrigieren.", svc, 1.0, RepairActionKind.FixZoneServerInfo));
                        }
                    }
                }

                if (svc.ClientPort is null)
                    issues.Add(Issue("NG-ZONE-0006", DiagnosticSeverity.Error, "Client-Port fehlt", $"Für Zone {zone} wurde in ServerInfo.txt kein ConnectionKind 20 gefunden.", "PG_W00_Zxx-Eintrag in ServerInfo.txt ergänzen.", svc, 1.0));
            }
        }

        foreach (var collision in portOwners.Where(x => x.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            issues.Add(new DiagnosticIssue
            {
                Code = "NG-NET-0002",
                Severity = DiagnosticSeverity.Critical,
                Title = "Port-Konflikt",
                Description = $"TCP-Port {collision.Key} ist mehreren Fiesta-Diensten zugeordnet: {string.Join(", ", collision.Value.Distinct())}.",
                Recommendation = "ServerInfo.txt sichern und jedem Endpunkt einen eindeutigen Port geben. Danach alle betroffenen Dienste neu starten.",
                Source = Path.Combine(serverRoot, "9Data", "ServerInfo", "ServerInfo.txt"),
                Evidence = $"Port {collision.Key}: {string.Join(", ", collision.Value)}",
                Confidence = 1.0
            });
        }

        return issues;
    }

    private static DiagnosticIssue Issue(string code, DiagnosticSeverity severity, string title, string description, string recommendation, FiestaServiceEntry svc, double confidence, RepairActionKind action = RepairActionKind.None) =>
        new()
        {
            Code = code,
            Severity = severity,
            Title = title,
            Description = description,
            Recommendation = recommendation,
            Source = svc.ConfigPath ?? svc.DirectoryPath,
            ServiceName = svc.ServiceName,
            ZoneNumber = svc.ZoneNumber,
            Confidence = confidence,
            RepairAction = action
        };

    public string BackupFile(string path)
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(path)!, ".nextgen-backups");
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, $"{Path.GetFileName(path)}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        File.Copy(path, backup, overwrite: false);
        return backup;
    }

    public void FixZoneServerInfo(FiestaServiceEntry zone)
    {
        if (zone.Kind != FiestaServiceKind.Zone || zone.ZoneNumber is not int n || string.IsNullOrWhiteSpace(zone.ConfigPath))
            throw new InvalidOperationException("Keine gültige Zone ausgewählt.");
        Directory.CreateDirectory(Path.GetDirectoryName(zone.ConfigPath)!);
        if (File.Exists(zone.ConfigPath)) BackupFile(zone.ConfigPath);
        var content = $"""
#DEFINE MY_SERVER
  <STRING>
  <STRING>
  <INTEGER>
  <INTEGER>
  <INTEGER>
#ENDDEFINE

MY_SERVER "_Zone{n}",        "_Zone{n}",    6,        0,        {n}

#include "../../9Data/ServerInfo/ServerInfo.txt"
#END
""";
        File.WriteAllText(zone.ConfigPath, content);
    }
}
