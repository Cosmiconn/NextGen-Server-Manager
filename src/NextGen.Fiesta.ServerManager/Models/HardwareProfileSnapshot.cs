namespace NextGen.Fiesta.ServerManager.Models;

public sealed class HardwareProfileSnapshot
{
    public string CpuModel { get; init; } = "unbekannt";
    public string SystemManufacturer { get; init; } = string.Empty;
    public string SystemModel { get; init; } = string.Empty;
    public int LogicalProcessors { get; init; }
    public int NumaNodes { get; init; } = 1;
    public int NominalMhz { get; init; }
    public double TotalMemoryMb { get; init; }
    public double AvailableMemoryMb { get; init; }
    public double SystemCpuPercent { get; init; }
    public double HighestZoneCorePercent { get; init; }
    public double AverageRunningZoneCorePercent { get; init; }
    public int RunningZones { get; init; }
    public double WorldManagerCorePercent { get; init; }
    public string ScalingMode { get; init; } = "unbekannt";
    public string Advice { get; init; } = string.Empty;

    public double MemoryUsedPercent => TotalMemoryMb > 0 ? (1.0 - AvailableMemoryMb / TotalMemoryMb) * 100.0 : 0;
    public string CpuClockText => NominalMhz > 0 ? $"~{NominalMhz / 1000d:F2} GHz nominal" : "Takt unbekannt";
    public string Summary => $"{(string.IsNullOrWhiteSpace(SystemModel) ? string.Empty : SystemModel + " · ")}{CpuModel} · {LogicalProcessors} logische CPUs · {NumaNodes} NUMA-Node(s) · {CpuClockText} · RAM {TotalMemoryMb / 1024d:F1} GB ({AvailableMemoryMb / 1024d:F1} GB frei) · Host-CPU {SystemCpuPercent:F0}%";
}
