using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class NetworkInspector
{
    public async Task<bool> IsPortOpenAsync(int port, string host = "127.0.0.1", int timeoutMs = 500, CancellationToken ct = default)
    {
        try
        {
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port))
                return true;
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, linked.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyDictionary<int, int>> GetListeningPidMapAsync(CommandRunner runner, CancellationToken ct = default)
    {
        var map = new Dictionary<int, int>();
        var result = await runner.RunAsync("netstat.exe", "-ano -p tcp", timeoutMs: 8000, cancellationToken: ct);
        foreach (var line in result.StdOut.Split('\n'))
        {
            var s = line.Trim();
            var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            var state = parts[3];
            if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase)
                && !state.Equals("ABHÖREN", StringComparison.OrdinalIgnoreCase)
                && !state.Equals("ABHOEREN", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryGetPort(parts[1], out var port)) continue;
            if (int.TryParse(parts[^1], out var pid)) map[port] = pid;
        }
        return map;
    }

    public async Task<int?> FindListeningPidAsync(int port, CommandRunner runner, CancellationToken ct = default)
    {
        var map = await GetListeningPidMapAsync(runner, ct);
        return map.TryGetValue(port, out var pid) ? pid : null;
    }


    public IReadOnlyDictionary<int, int> GetEstablishedConnectionCounts(IEnumerable<int> localPorts)
    {
        var wanted = new HashSet<int>(localPorts.Where(x => x > 0));
        var counts = wanted.ToDictionary(x => x, _ => 0);
        try
        {
            foreach (var c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
            {
                if (c.State != TcpState.Established) continue;
                var port = c.LocalEndPoint.Port;
                if (wanted.Contains(port)) counts[port]++;
            }
        }
        catch { }
        return counts;
    }

    private static bool TryGetPort(string endpoint, out int port)
    {
        port = 0;
        // IPv4, IPv6 and wildcard forms emitted by netstat are all easiest to handle from the last colon.
        var index = endpoint.LastIndexOf(':');
        return index >= 0 && int.TryParse(endpoint[(index + 1)..], out port);
    }
}
