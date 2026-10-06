using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class ServerInfoParser
{
    private static readonly Regex Rx = new(
        "^\\s*SERVER_INFO\\s+\"(?<name>[^\"]+)\"\\s*,\\s*(?<type>-?\\d+)\\s*,\\s*(?<world>-?\\d+)\\s*,\\s*(?<zone>-?\\d+)\\s*,\\s*(?<kind>-?\\d+)\\s*,\\s*\"(?<host>[^\"]+)\"\\s*,\\s*(?<port>\\d+)\\s*,\\s*(?<backlog>\\d+)\\s*,\\s*(?<maxaccept>\\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<ServerInfoEntry> Parse(string path)
    {
        if (!File.Exists(path)) return Array.Empty<ServerInfoEntry>();
        var result = new List<ServerInfoEntry>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split(';', 2)[0];
            var m = Rx.Match(line);
            if (!m.Success) continue;
            result.Add(new ServerInfoEntry(
                m.Groups["name"].Value,
                int.Parse(m.Groups["type"].Value),
                int.Parse(m.Groups["world"].Value),
                int.Parse(m.Groups["zone"].Value),
                int.Parse(m.Groups["kind"].Value),
                m.Groups["host"].Value,
                int.Parse(m.Groups["port"].Value),
                int.Parse(m.Groups["backlog"].Value),
                int.Parse(m.Groups["maxaccept"].Value)));
        }
        return result;
    }
}
