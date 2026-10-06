namespace NextGen.Fiesta.ServerManager.Models;

public sealed class WorldManagerCapacitySnapshot
{
    public int ClientSessions { get; init; }
    public int ClientLimit { get; init; } = 1500;
    public int ZoneSessions { get; init; }
    public int ZoneSessionLimit { get; init; } = 100;
    public double CpuCorePercent { get; init; }
    public double PrivateMemoryMb { get; init; }
    public string Pressure { get; init; } = "Unbekannt";
    public string Recommendation { get; init; } = string.Empty;
    public string Summary => $"WorldManager: Clients {ClientSessions:N0}/{ClientLimit:N0} · Zone-Sessions {ZoneSessions:N0}/{ZoneSessionLimit:N0} · CPU-Core {CpuCorePercent:F0}% · Privat-RAM {PrivateMemoryMb:F0} MB · {Pressure}";
}
