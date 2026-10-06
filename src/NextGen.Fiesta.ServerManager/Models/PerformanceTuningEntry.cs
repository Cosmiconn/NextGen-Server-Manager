namespace NextGen.Fiesta.ServerManager.Models;

public sealed class PerformanceTuningEntry
{
    public string Scope { get; init; } = string.Empty;
    public string Resource { get; init; } = string.Empty;
    public int CurrentLimit { get; init; }
    public int SuggestedTarget { get; init; }
    public string Method { get; init; } = string.Empty;
    public double? ExtraRawMemoryMb { get; init; }
    public string Risk { get; init; } = string.Empty;
    public string Impact { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;

    public string CurrentText => CurrentLimit > 0 ? CurrentLimit.ToString("N0") : "–";
    public string TargetText => SuggestedTarget > 0 ? SuggestedTarget.ToString("N0") : "–";
    public string MemoryText => ExtraRawMemoryMb.HasValue ? $"+{ExtraRawMemoryMb.Value:F1} MB" : "nicht bestimmt";
}

public sealed class ProcessScalingSnapshot
{
    public string Component { get; init; } = string.Empty;
    public string BinaryPath { get; init; } = string.Empty;
    public bool IsPe32 { get; init; }
    public bool LargeAddressAware { get; init; }
    public bool HasIocpWorkers { get; init; }
    public bool HasMainThreadSymbol { get; init; }
    public double CpuCorePercent { get; init; }
    public double PrivateMemoryMb { get; init; }
    public double VirtualMemoryMb { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public int ClientSessions { get; init; }
    public int ClientLimit { get; init; }
    public int? EstimatedCpuCeiling { get; init; }
    public string VerticalHeadroom { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;

    public string ArchitectureText => IsPe32 ? (LargeAddressAware ? "PE32 + LAA" : "PE32") : "unbekannt";
    public string ThreadModelText => HasIocpWorkers
        ? (HasMainThreadSymbol ? "IOCP + Mainthread" : "IOCP")
        : (HasMainThreadSymbol ? "Mainthread erkannt" : "nicht verifiziert");
    public string CpuText => $"{CpuCorePercent:F0}% Core";
    public string MemoryText => $"{PrivateMemoryMb:F0} MB privat / {VirtualMemoryMb:F0} MB virtuell";
    public string SessionText => ClientLimit > 0 ? $"{ClientSessions:N0}/{ClientLimit:N0}" : ClientSessions.ToString("N0");
    public string EstimatedCpuCeilingText => EstimatedCpuCeiling.HasValue ? $"~{EstimatedCpuCeiling.Value:N0}" : "lernt / zu wenig Last";
}

public sealed class PerformanceTuningAuditResult
{
    public IReadOnlyList<PerformanceTuningEntry> Candidates { get; init; } = Array.Empty<PerformanceTuningEntry>();
    public IReadOnlyList<ProcessScalingSnapshot> Processes { get; init; } = Array.Empty<ProcessScalingSnapshot>();
    public string Summary { get; init; } = string.Empty;
}
