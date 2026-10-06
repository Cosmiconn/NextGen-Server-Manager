using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class FiestaTopologyScanner
{
    private readonly ServerInfoParser _serverInfoParser = new();
    private static readonly Regex ZoneDirRx = new("^Zone(?<n>\\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string Directory, string Service, FiestaServiceKind Kind, string Exe)[] Core =
    {
        ("Account", "_Account", FiestaServiceKind.Account, "Account.exe"),
        ("AccountLog", "_AccountLog", FiestaServiceKind.AccountLog, "AccountLog.exe"),
        ("Login", "_Login", FiestaServiceKind.Login, "Login.exe"),
        ("Character", "_Character", FiestaServiceKind.Character, "Character.exe"),
        ("GameLog", "_GameLog", FiestaServiceKind.GameLog, "GameLog.exe"),
        ("WorldManager", "_WorldManager", FiestaServiceKind.WorldManager, "WorldManager.exe"),
        ("GamigoZR", "_GamigoZR", FiestaServiceKind.GamigoZR, "GamigoZR.exe")
    };

    public bool IsValidRoot(string root) =>
        Directory.Exists(root) && Directory.Exists(Path.Combine(root, "9Data")) && Directory.Exists(Path.Combine(root, "WorldManager"));

    public IReadOnlyList<FiestaServiceEntry> Scan(string root)
    {
        var serverInfoPath = Path.Combine(root, "9Data", "ServerInfo", "ServerInfo.txt");
        var serverInfo = _serverInfoParser.Parse(serverInfoPath);
        var result = new List<FiestaServiceEntry>();

        foreach (var core in Core)
        {
            var dir = Path.Combine(root, core.Directory);
            if (!Directory.Exists(dir)) continue;
            var entry = new FiestaServiceEntry
            {
                DisplayName = core.Directory,
                ServiceName = core.Service,
                Kind = core.Kind,
                DirectoryPath = dir,
                ExecutablePath = Path.Combine(dir, core.Exe),
                ConfigPath = FindCoreConfig(dir, core.Kind),
                PdbPath = Directory.EnumerateFiles(dir, "*.pdb", SearchOption.TopDirectoryOnly).FirstOrDefault()
            };
            ApplyPorts(entry, serverInfo);
            result.Add(entry);
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "Zone*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dir);
            var m = ZoneDirRx.Match(name);
            if (!m.Success) continue;
            var zone = int.Parse(m.Groups["n"].Value);
            var entry = new FiestaServiceEntry
            {
                DisplayName = $"Zone{zone:00}",
                ServiceName = $"_Zone{zone}",
                Kind = FiestaServiceKind.Zone,
                ZoneNumber = zone,
                DirectoryPath = dir,
                ExecutablePath = Path.Combine(dir, "Zone.exe"),
                ConfigPath = Path.Combine(dir, "ZoneServerInfo", "ZoneServerInfo.txt"),
                PdbPath = Directory.EnumerateFiles(dir, "*.pdb", SearchOption.TopDirectoryOnly).FirstOrDefault()
            };
            ApplyPorts(entry, serverInfo);
            result.Add(entry);
        }

        return result.OrderBy(x => x.Kind == FiestaServiceKind.Zone ? 1 : 0)
            .ThenBy(x => x.Kind == FiestaServiceKind.Zone ? x.ZoneNumber : (int)x.Kind)
            .ToList();
    }

    private static string? FindCoreConfig(string dir, FiestaServiceKind kind) => kind switch
    {
        FiestaServiceKind.WorldManager => Path.Combine(dir, "WMServerInfo.txt"),
        FiestaServiceKind.Login => Path.Combine(dir, "LoginServerInfo.txt"),
        FiestaServiceKind.Account => Path.Combine(dir, "DataServerInfo_Account.txt"),
        FiestaServiceKind.AccountLog => Path.Combine(dir, "DataServerInfo_AccountLog.txt"),
        FiestaServiceKind.Character => Path.Combine(dir, "DataServerInfo_Character.txt"),
        FiestaServiceKind.GameLog => Path.Combine(dir, "DataServerInfo_GameLog.txt"),
        _ => null
    };

    private static void ApplyPorts(FiestaServiceEntry entry, IReadOnlyList<ServerInfoEntry> infos)
    {
        if (entry.Kind == FiestaServiceKind.Zone && entry.ZoneNumber is int z)
        {
            entry.ClientPort = infos.FirstOrDefault(x => x.ServerType == 6 && x.ZoneNo == z && x.ConnectionKind == 20)?.Port;
            entry.InternalPort = infos.FirstOrDefault(x => x.ServerType == 6 && x.ZoneNo == z && x.ConnectionKind == 6)?.Port;
            entry.OptoolPort = infos.FirstOrDefault(x => x.ServerType == 6 && x.ZoneNo == z && x.ConnectionKind == 8)?.Port;
        }
        else if (entry.Kind == FiestaServiceKind.WorldManager)
        {
            entry.ClientPort = infos.FirstOrDefault(x => x.ServerType == 5 && x.ConnectionKind == 20)?.Port;
            entry.InternalPort = infos.FirstOrDefault(x => x.ServerType == 5 && x.ConnectionKind == 6)?.Port;
            entry.OptoolPort = infos.FirstOrDefault(x => x.ServerType == 5 && x.ConnectionKind == 8)?.Port;
        }
        else
        {
            var serverType = entry.Kind switch
            {
                FiestaServiceKind.Account => 0,
                FiestaServiceKind.AccountLog => 1,
                FiestaServiceKind.Character => 2,
                FiestaServiceKind.GameLog => 3,
                FiestaServiceKind.Login => 4,
                _ => -1
            };
            var first = infos.FirstOrDefault(x => x.ServerType == serverType);
            entry.ClientPort = first?.Port;
        }
    }
}
