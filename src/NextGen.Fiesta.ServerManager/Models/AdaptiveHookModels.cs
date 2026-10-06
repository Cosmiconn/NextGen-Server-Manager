namespace NextGen.Fiesta.ServerManager.Models;

public sealed class AdaptiveHookAssessment
{
    public string Scope { get; init; } = string.Empty;
    public string Resource { get; init; } = string.Empty;
    public string HookType { get; init; } = string.Empty;
    public int CurrentLimit { get; init; }
    public int TargetLimit { get; init; }
    public string Decision { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public double CpuCorePercent { get; init; }
    public double CurrentPrivateMemoryMb { get; init; }
    public double ProjectedPrivateMemoryMb { get; init; }
    public double ExtraMemoryMb { get; init; }
    public bool CanApply { get; init; }
    public bool RestartRequired { get; init; }

    public string CurrentText => CurrentLimit > 0 ? CurrentLimit.ToString("N0") : "–";
    public string TargetText => TargetLimit > 0 ? TargetLimit.ToString("N0") : "–";
    public string CpuText => CpuCorePercent > 0 ? $"{CpuCorePercent:F0}% Core" : "–";
    public string MemoryText => ProjectedPrivateMemoryMb > 0
        ? $"{CurrentPrivateMemoryMb:F0} → {ProjectedPrivateMemoryMb:F0} MB"
        : "–";
    public string ApplyText => CanApply ? (RestartRequired ? "JA · Restart" : "JA") : "NEIN";
}

public sealed class AdaptiveHookAuditResult
{
    public IReadOnlyList<AdaptiveHookAssessment> Assessments { get; init; } = Array.Empty<AdaptiveHookAssessment>();
    public string Summary { get; init; } = string.Empty;
    public bool HasBlockingIssue => Assessments.Any(x => x.Decision == "BLOCKIERT");
}

public sealed record AdaptiveHookApplyResult(
    bool Success,
    string Summary,
    string BackupDirectory,
    bool RestartRequired,
    bool RuntimeWorldManagerLimitApplied);
