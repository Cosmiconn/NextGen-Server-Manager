using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class ZoneCapacityMonitor
{
    private const int PlayerLimit = 1500;
    private const int MapBlockLimit = 256;
    private const int MapClusterLimit = 512;
    private readonly Dictionary<int, Queue<(DateTime Time, double Score)>> _history = new();
    private readonly WorldManagerCapacityProbe _worldManagerProbe = new();
    private readonly ZoneObjectPoolProbe _zoneObjectPoolProbe = new();

    public IReadOnlyList<ZoneCapacitySnapshot> Build(
        IReadOnlyList<FiestaServiceEntry> services,
        IReadOnlyList<MapCapacityEntry> maps,
        AppSettings settings,
        IReadOnlyList<DiagnosticIssue>? diagnostics = null)
    {
        var now = DateTime.Now;
        var result = new List<ZoneCapacitySnapshot>();

        foreach (var zone in services.Where(x => x.Kind == FiestaServiceKind.Zone && x.ZoneNumber.HasValue).OrderBy(x => x.ZoneNumber))
        {
            var number = zone.ZoneNumber!.Value;
            var configuredMaps = maps.Count(x => IsAssignedToZone(x.Zones, number));
            var clientPercent = Percent(zone.EstablishedClientConnections, PlayerLimit);
            var mapPercent = Percent(configuredMaps, MapBlockLimit);
            var memoryPercent = settings.ZonePrivateMemoryBudgetMb > 0
                ? Math.Max(0, zone.PrivateMemoryMb / settings.ZonePrivateMemoryBudgetMb * 100.0)
                : 0;
            // Core-equivalent CPU is intentionally used here. A mostly single-threaded Zone can be saturated
            // while Windows still reports a small percentage of total machine CPU on many-core hosts.
            var cpuPressure = Math.Clamp(zone.CpuCorePercent, 0, 100);

            var pools = _zoneObjectPoolProbe.Read(zone);
            var playerPoolPercent = pools.RuntimeVerified ? Percent(pools.PlayerCount, pools.PlayerLimit) : 0;
            var mobPoolPercent = pools.RuntimeVerified ? Percent(pools.MobCount, pools.MobLimit) : 0;
            var npcPoolPercent = pools.RuntimeVerified ? Percent(pools.NpcCount, pools.NpcLimit) : 0;
            var objectPoolPercent = new[] { playerPoolPercent, mobPoolPercent, npcPoolPercent }.Max();

            var hardLimitEvent = FindHardLimitEvent(diagnostics, number);
            var overall = hardLimitEvent is null
                ? new[] { clientPercent, mapPercent, memoryPercent, cpuPressure, objectPoolPercent }.Max()
                : 100.0;

            var trend = TrackTrend(number, overall, now);
            var pressure = hardLimitEvent is null
                ? Classify(zone, clientPercent, mapPercent, memoryPercent, cpuPressure, objectPoolPercent, settings)
                : "KRITISCH";
            var recommendation = hardLimitEvent is null
                ? Recommend(zone, configuredMaps, clientPercent, mapPercent, memoryPercent, cpuPressure, pools, trend, pressure, settings)
                : $"Harter Pool-/Map-Grenzfehler im Log: {hardLimitEvent.Title}. Neue Zone/Lastverlagerung prüfen.";

            result.Add(new ZoneCapacitySnapshot
            {
                ZoneNumber = number,
                State = zone.State.ToString(),
                ClientConnections = zone.EstablishedClientConnections,
                ClientLimit = PlayerLimit,
                ClientPercent = clientPercent,
                CpuTotalPercent = zone.CpuPercent,
                CpuCorePercent = zone.CpuCorePercent,
                WorkingSetMb = zone.WorkingSetMb,
                PrivateMemoryMb = zone.PrivateMemoryMb,
                VirtualMemoryMb = zone.VirtualMemoryMb,
                ConfiguredMaps = configuredMaps,
                MapBlockLimit = MapBlockLimit,
                MapClusterLimit = MapClusterLimit,
                ObjectPoolsVerified = pools.RuntimeVerified,
                PlayerPoolCount = pools.PlayerCount,
                PlayerPoolLimit = pools.PlayerLimit,
                MobPoolCount = pools.MobCount,
                MobPoolLimit = pools.MobLimit,
                NpcPoolCount = pools.NpcCount,
                NpcPoolLimit = pools.NpcLimit,
                ObjectPoolDetail = pools.Detail,
                ObjectPoolPercent = objectPoolPercent,
                OverallPercent = overall,
                Pressure = pressure,
                Trend = trend,
                Recommendation = recommendation,
                Timestamp = now
            });
        }

        return result;
    }

    public WorldManagerCapacitySnapshot BuildWorldManager(IReadOnlyList<FiestaServiceEntry> services, AppSettings settings)
    {
        var wm = services.FirstOrDefault(x => x.Kind == FiestaServiceKind.WorldManager);
        if (wm is null)
            return new WorldManagerCapacitySnapshot
            {
                Pressure = "NICHT GEFUNDEN",
                Recommendation = "WorldManager nicht erkannt."
            };

        var probe = _worldManagerProbe.Read(settings.ServerRoot, wm);
        var clientSessions = probe.RuntimeVerified ? probe.RuntimeNumSessions : wm.EstablishedClientConnections;
        var clientLimit = Math.Max(1, probe.ActiveClientLimit);
        var zoneLimit = Math.Max(1, probe.ConfiguredZoneSessionLimit);
        var clientPct = Percent(clientSessions, clientLimit);
        var zonePct = Percent(wm.EstablishedInternalConnections, zoneLimit);
        var memoryPct = settings.ZonePrivateMemoryBudgetMb > 0 ? wm.PrivateMemoryMb / settings.ZonePrivateMemoryBudgetMb * 100.0 : 0;
        var cpuPct = Math.Clamp(wm.CpuCorePercent, 0, 100);
        var max = new[] { clientPct, zonePct, memoryPct, cpuPct }.Max();
        var pressure = wm.State != ServiceRuntimeState.Running ? "NICHT BEREIT"
            : max >= settings.ZoneCriticalPercent ? "KRITISCH"
            : max >= settings.ZoneScaleRecommendPercent ? "AUSBAU"
            : max >= settings.ZoneWarningPercent ? "WARNUNG" : "OK";

        string recommendation;
        if (probe.RestartPending)
        {
            recommendation = $"WorldManager-Konfiguration und aktiver Hard-Pool unterscheiden sich: aktiv {probe.RuntimeMaxSessions:N0}, ServerInfo {probe.ConfiguredClientLimit:N0}. WorldManager kontrolliert neu starten, bevor die höhere Kapazität als aktiv bewertet wird.";
        }
        else if (zonePct >= settings.ZoneWarningPercent)
        {
            recommendation = $"WM-Zone-Sessionlistener nähert sich dem aktuell konfigurierten Limit {zoneLimit:N0}; offene/duplizierte S2S-Verbindungen prüfen.";
        }
        else if (clientPct >= settings.ZoneWarningPercent)
        {
            recommendation = $"WM-Clientpool nähert sich dem aktiv gemessenen Limit {clientLimit:N0}; Login-/World-Verteilung prüfen.";
        }
        else if (pressure is "KRITISCH" or "AUSBAU")
        {
            recommendation = "WM CPU/RAM-Kapazität prüfen; eine zusätzliche Zone entlastet nicht automatisch den WorldManager.";
        }
        else
        {
            recommendation = probe.RuntimeVerified
                ? "WorldManager besitzt nach hash-verifizierten Runtimewerten Reserve."
                : $"WorldManager besitzt nach aktuell messbaren Werten Reserve. Limitquelle: {probe.Source}.";
        }

        return new WorldManagerCapacitySnapshot
        {
            ClientSessions = clientSessions,
            ClientLimit = clientLimit,
            ClientHardLimit = probe.RuntimeVerified ? probe.RuntimeMaxSessions : clientLimit,
            ConfiguredClientLimit = probe.ConfiguredClientLimit,
            RuntimeUserLimit = probe.RuntimeVerified ? probe.RuntimeUserLimit : clientLimit,
            RuntimeVerified = probe.RuntimeVerified,
            RestartPending = probe.RestartPending,
            LimitSource = probe.Source,
            LimitDetail = probe.Detail,
            ZoneSessions = wm.EstablishedInternalConnections,
            ZoneSessionLimit = zoneLimit,
            CpuCorePercent = wm.CpuCorePercent,
            PrivateMemoryMb = wm.PrivateMemoryMb,
            Pressure = pressure,
            Recommendation = recommendation
        };
    }

    private static bool IsAssignedToZone(string zones, int zone)
    {
        if (string.IsNullOrWhiteSpace(zones)) return false;
        var a = $"Z{zone:00}";
        var b = zone.ToString();
        return zones.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase) || x.Equals(b, StringComparison.OrdinalIgnoreCase));
    }

    private static DiagnosticIssue? FindHardLimitEvent(IReadOnlyList<DiagnosticIssue>? diagnostics, int zone)
    {
        if (diagnostics is null) return null;
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NG-ZONE-0014", // BlockInfo
            "NG-ZONE-0015", // Mob
            "NG-ZONE-0020", // NPC
            "NG-ZONE-0021", // MapCluster
            "NG-ZONE-0022"  // BlockDistribute
        };
        var cutoff = DateTime.Now - TimeSpan.FromMinutes(5);
        return diagnostics.FirstOrDefault(x =>
        {
            if (!codes.Contains(x.Code) || x.Timestamp < cutoff) return false;
            if (!(x.ZoneNumber == zone || x.ServiceName?.Equals($"_Zone{zone}", StringComparison.OrdinalIgnoreCase) == true)) return false;

            // A historical file without a parseable timestamp used to be stamped with DateTime.Now
            // by LogAnalyzer and could therefore look like a fresh capacity incident. Require a
            // recently written source file when the diagnostic came from a file path.
            try
            {
                if (File.Exists(x.Source) && File.GetLastWriteTime(x.Source) < cutoff) return false;
            }
            catch { }
            return true;
        });
    }

    private static double Percent(double value, double limit) => limit <= 0 ? 0 : Math.Max(0, value / limit * 100.0);

    private string TrackTrend(int zone, double score, DateTime now)
    {
        if (!_history.TryGetValue(zone, out var q)) _history[zone] = q = new Queue<(DateTime, double)>();
        q.Enqueue((now, score));
        while (q.Count > 0 && now - q.Peek().Time > TimeSpan.FromMinutes(10)) q.Dequeue();

        var recent = q.Where(x => now - x.Time <= TimeSpan.FromMinutes(1)).Select(x => x.Score).ToArray();
        var older = q.Where(x => now - x.Time > TimeSpan.FromMinutes(1) && now - x.Time <= TimeSpan.FromMinutes(5)).Select(x => x.Score).ToArray();
        if (recent.Length < 2 || older.Length < 2) return "lernt";
        var delta = recent.Average() - older.Average();
        return delta >= 7 ? "↑ stark steigend" : delta >= 3 ? "↗ steigend" : delta <= -7 ? "↓ stark fallend" : delta <= -3 ? "↘ fallend" : "→ stabil";
    }

    private static string Classify(
        FiestaServiceEntry zone,
        double players,
        double maps,
        double memory,
        double cpu,
        double objectPools,
        AppSettings settings)
    {
        if (zone.State != ServiceRuntimeState.Running) return zone.State == ServiceRuntimeState.Stopped ? "GESTOPPT" : "NICHT BEREIT";
        if (new[] { players, maps, memory, cpu, objectPools }.Any(x => x >= settings.ZoneCriticalPercent))
            return "KRITISCH";
        if (new[] { players, maps, memory, cpu, objectPools }.Any(x => x >= settings.ZoneScaleRecommendPercent))
            return "AUSBAU";
        if (new[] { players, maps, memory, cpu, objectPools }.Any(x => x >= settings.ZoneWarningPercent))
            return "WARNUNG";
        return "OK";
    }

    private static string Recommend(
        FiestaServiceEntry zone,
        int configuredMaps,
        double players,
        double maps,
        double memory,
        double cpu,
        ZoneObjectPoolRuntimeSnapshot pools,
        string trend,
        string pressure,
        AppSettings settings)
    {
        if (zone.State != ServiceRuntimeState.Running)
            return "Kapazität erst nach laufendem Dienst bewerten.";

        var reasons = new List<string>();
        if (players >= settings.ZoneWarningPercent) reasons.Add($"Clients {players:F0}%");
        if (maps >= settings.ZoneWarningPercent) reasons.Add($"Maps {configuredMaps}/{MapBlockLimit}");
        if (memory >= settings.ZoneWarningPercent) reasons.Add($"Privat-RAM {zone.PrivateMemoryMb:F0}/{settings.ZonePrivateMemoryBudgetMb} MB");
        if (cpu >= settings.ZoneWarningPercent) reasons.Add($"CPU-Core {cpu:F0}%");

        if (pools.RuntimeVerified)
        {
            var playerPct = Percent(pools.PlayerCount, pools.PlayerLimit);
            var mobPct = Percent(pools.MobCount, pools.MobLimit);
            var npcPct = Percent(pools.NpcCount, pools.NpcLimit);
            if (playerPct >= settings.ZoneWarningPercent) reasons.Add($"ShinePlayer {pools.PlayerCount:N0}/{pools.PlayerLimit:N0}");
            if (mobPct >= settings.ZoneWarningPercent) reasons.Add($"ShineMob {pools.MobCount:N0}/{pools.MobLimit:N0}");
            if (npcPct >= settings.ZoneWarningPercent) reasons.Add($"ShineNPC {pools.NpcCount:N0}/{pools.NpcLimit:N0}");
        }

        if (pressure == "KRITISCH") return "Neue Zone jetzt bereitstellen und Last/Maps verlagern: " + string.Join(" · ", reasons);
        if (pressure == "AUSBAU") return "Neue Zone vorbereiten; Verlagerung planen: " + string.Join(" · ", reasons);
        if (pressure == "WARNUNG" && trend.Contains("steigend", StringComparison.OrdinalIgnoreCase))
            return "Last steigt: Zone vorsorglich planen. " + string.Join(" · ", reasons);
        if (pressure == "WARNUNG") return "Beobachten und Kapazitätsreserve prüfen: " + string.Join(" · ", reasons);
        return pools.RuntimeVerified
            ? $"Genügend Reserve. Objektpools: {pools.CompactText}."
            : "Genügend Reserve nach aktuell messbaren Laufzeitwerten; Objektpoolbelegung nicht verifiziert.";
    }
}
