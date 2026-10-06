namespace NextGen.Fiesta.ServerManager.Models;

public sealed class WorldManagerCapacitySnapshot
{
    public int ClientSessions { get; init; }
    public int ClientLimit { get; init; } = 1500;
    public int ClientHardLimit { get; init; } = 1500;
    public int ConfiguredClientLimit { get; init; } = 1500;
    public int RuntimeUserLimit { get; init; } = 1500;
    public bool RuntimeVerified { get; init; }
    public bool RestartPending { get; init; }
    public string LimitSource { get; init; } = "Stock";
    public string LimitDetail { get; init; } = string.Empty;
    public int ZoneSessions { get; init; }
    public int ZoneSessionLimit { get; init; } = 100;
    public double CpuCorePercent { get; init; }
    public double PrivateMemoryMb { get; init; }
    public string Pressure { get; init; } = "Unbekannt";
    public string Recommendation { get; init; } = string.Empty;

    public string Summary
    {
        get
        {
            var runtimeSuffix = RestartPending
                ? $" · Hard-Pool {ClientHardLimit:N0} · Config {ConfiguredClientLimit:N0} (Restart offen)"
                : RuntimeVerified && ClientHardLimit != ClientLimit
                    ? $" · Hard-Pool {ClientHardLimit:N0}"
                    : string.Empty;
            return $"WorldManager: Clients {ClientSessions:N0}/{ClientLimit:N0}{runtimeSuffix} · Zone-Sessions {ZoneSessions:N0}/{ZoneSessionLimit:N0} · CPU-Core {CpuCorePercent:F0}% · Privat-RAM {PrivateMemoryMb:F0} MB · {Pressure}";
        }
    }
}
