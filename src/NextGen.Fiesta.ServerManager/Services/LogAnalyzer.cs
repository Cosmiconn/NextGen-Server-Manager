using System.Text.Json;
using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class LogAnalyzer
{
    private readonly List<DiagnosticRule> _rules = new();
    private static readonly Regex PacketRx = new(@"dep\s*=\s*(?<dep>\d+)\s*,?\s*cmd\s*=\s*(?<cmd>\d+)\s*,?\s*(?:length|len)\s*=\s*(?<len>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimestampRx = new(@"(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})", RegexOptions.Compiled);

    private static readonly Dictionary<ushort, string> SeedOpcodeNames = new()
    {
        [0x7495] = "NC_GUILD_GUILDWARSTATUS_REQ",
        [0x7496] = "NC_GUILD_GUILDWARSTATUS_ACK",
        [0x0C17] = "NC_USER_CONNECTCUT_CMD",
        [0x0C19] = "NC_USER_CONNECTCUT2ZONE_CMD",
        [0x0C1A] = "NC_USER_CONNECTCUT2WORLDMANAGER_CMD",
        [0x1801] = "NC_MAP_LOGIN_REQ",
        [0x1804] = "NC_MAP_LOGINFAIL_ACK"
    };

    public LogAnalyzer()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "diagnostic-rules.json");
        if (File.Exists(path))
        {
            try { _rules = JsonSerializer.Deserialize<List<DiagnosticRule>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
            catch { }
        }
    }

    public LogEvent ParseEvent(string line, string source)
    {
        var packet = PacketRx.Match(line);
        int? dep = packet.Success ? int.Parse(packet.Groups["dep"].Value) : null;
        int? cmd = packet.Success ? int.Parse(packet.Groups["cmd"].Value) : null;
        int? len = packet.Success ? int.Parse(packet.Groups["len"].Value) : null;
        var opcode = dep is not null && cmd is not null ? (ushort?)(((dep.Value & 0x3F) << 10) | (cmd.Value & 0x3FF)) : null;
        var name = opcode is not null && SeedOpcodeNames.TryGetValue(opcode.Value, out var packetName) ? packetName : null;
        var severity = GuessSeverity(line);
        var code = MatchRule(line)?.Code;
        var message = line.Trim();
        if (opcode is not null)
            message += $"  [0x{opcode.Value:X4}{(name is null ? string.Empty : " " + name)}]";
        return new LogEvent(ParseTimestamp(line) ?? DateTime.Now, source, message, severity, code, dep, cmd, len);
    }

    public IReadOnlyList<DiagnosticIssue> Analyze(IEnumerable<string> lines, string source, string? serviceName = null, int? zoneNumber = null)
    {
        var issues = new List<DiagnosticIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var packet = PacketRx.Match(line);
            int? dep = packet.Success ? int.Parse(packet.Groups["dep"].Value) : null;
            int? cmd = packet.Success ? int.Parse(packet.Groups["cmd"].Value) : null;
            int? len = packet.Success ? int.Parse(packet.Groups["len"].Value) : null;
            var opcode = dep is not null && cmd is not null ? (ushort?)(((dep.Value & 0x3F) << 10) | (cmd.Value & 0x3FF)) : null;
            var packetName = opcode is not null && SeedOpcodeNames.TryGetValue(opcode.Value, out var name) ? name : null;

            foreach (var rule in _rules)
            {
                var matched = rule.Regex
                    ? Regex.IsMatch(line, rule.Pattern, RegexOptions.IgnoreCase)
                    : line.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
                if (!matched) continue;

                var key = $"{rule.Code}|{serviceName}|{zoneNumber}|{packetName}";
                if (!seen.Add(key)) continue;

                var desc = rule.Description;
                var evidence = line.Trim();
                if (opcode is not null)
                {
                    desc += $" Erkannter Opcode: 0x{opcode.Value:X4}";
                    if (packetName is not null) desc += $" ({packetName}).";
                    desc += $" Department={dep}, Command={cmd}, Length={len}.";
                }

                issues.Add(new DiagnosticIssue
                {
                    Code = rule.Code,
                    Severity = Enum.TryParse<DiagnosticSeverity>(rule.Severity, true, out var sev) ? sev : DiagnosticSeverity.Warning,
                    Title = rule.Title,
                    Description = desc,
                    Recommendation = rule.Recommendation,
                    Source = source,
                    ServiceName = serviceName,
                    ZoneNumber = zoneNumber,
                    Evidence = evidence,
                    Confidence = packetName is null ? 0.80 : 0.96,
                    RepairAction = Enum.TryParse<RepairActionKind>(rule.RepairAction, true, out var action) ? action : RepairActionKind.None,
                    Timestamp = ParseTimestamp(line) ?? DateTime.Now
                });
            }
        }
        return issues;
    }

    private DiagnosticRule? MatchRule(string line)
    {
        foreach (var rule in _rules)
        {
            var matched = rule.Regex
                ? Regex.IsMatch(line, rule.Pattern, RegexOptions.IgnoreCase)
                : line.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
            if (matched) return rule;
        }
        return null;
    }

    private static DiagnosticSeverity GuessSeverity(string line)
    {
        if (line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || line.Contains("ASSERT", StringComparison.OrdinalIgnoreCase)) return DiagnosticSeverity.Critical;
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || line.Contains("EXCEPTION", StringComparison.OrdinalIgnoreCase)) return DiagnosticSeverity.Error;
        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase) || line.Contains("RECONNECT", StringComparison.OrdinalIgnoreCase) || line.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase)) return DiagnosticSeverity.Warning;
        return DiagnosticSeverity.Info;
    }

    private static DateTime? ParseTimestamp(string line)
    {
        var m = TimestampRx.Match(line);
        return m.Success && DateTime.TryParse($"{m.Groups["date"].Value} {m.Groups["time"].Value}", out var dt) ? dt : null;
    }
}
