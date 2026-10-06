namespace NextGen.Fiesta.ServerManager.Models;

public sealed class ZoneCapacitySnapshot
{
    public int ZoneNumber { get; init; }
    public string ZoneName => $"Zone{ZoneNumber:00}";
    public string State { get; init; } = string.Empty;
    public int ClientConnections { get; init; }
    public int ClientLimit { get; init; } = 1500;
    public double ClientPercent { get; init; }
    public double CpuTotalPercent { get; init; }
    public double CpuCorePercent { get; init; }
    public double WorkingSetMb { get; init; }
    public double PrivateMemoryMb { get; init; }
    public double VirtualMemoryMb { get; init; }
    public int ConfiguredMaps { get; init; }
    public int MapBlockLimit { get; init; } = 256;
    public int MapClusterLimit { get; init; } = 512;
    public double OverallPercent { get; init; }
    public string Pressure { get; init; } = "Unbekannt";
    public string Trend { get; init; } = "–";
    public string Recommendation { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public string ClientText => $"{ClientConnections:N0} / {ClientLimit:N0}";
    public string ClientPercentText => $"{ClientPercent:F1}%";
    public string CpuText => CpuCorePercent > 0.1 ? $"{CpuCorePercent:F0}% Core / {CpuTotalPercent:F1}% Gesamt" : $"{CpuTotalPercent:F1}%";
    public string MemoryText => PrivateMemoryMb > 0 ? $"{PrivateMemoryMb:F0} MB privat / {WorkingSetMb:F0} MB WS" : "–";
    public string MapText => $"{ConfiguredMaps:N0} / {MapBlockLimit:N0}";
    public string OverallText => $"{OverallPercent:F0}%";
}
