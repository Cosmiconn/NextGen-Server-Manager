namespace NextGen.Fiesta.ServerManager.Models;

public sealed class WorldManagerLimitProbeResult
{
    public bool RuntimeVerified { get; init; }
    public int RuntimeNumSessions { get; init; }
    public int RuntimeMaxSessions { get; init; }
    public int RuntimeUserLimit { get; init; }
    public int ConfiguredClientLimit { get; init; } = 1500;
    public int ConfiguredZoneSessionLimit { get; init; } = 100;
    public int ActiveClientLimit { get; init; } = 1500;
    public bool RestartPending { get; init; }
    public string Source { get; init; } = "Stock-Fallback";
    public string Detail { get; init; } = string.Empty;
}
