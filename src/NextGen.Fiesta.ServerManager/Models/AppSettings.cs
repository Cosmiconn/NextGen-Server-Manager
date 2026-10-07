namespace NextGen.Fiesta.ServerManager.Models;

public sealed class AppSettings
{
    public string ServerRoot { get; set; } = string.Empty;
    public string? ReferenceRoot { get; set; }
    public string? PdbRoot { get; set; }
    public string? ClientBinaryPath { get; set; }
    public int RefreshIntervalSeconds { get; set; } = 3;
    public int WorldManagerReadyTimeoutSeconds { get; set; } = 90;
    public int ZoneReadyTimeoutSeconds { get; set; } = 45;
    public int ZoneStartGapSeconds { get; set; } = 2;
    public bool AutoAnalyzeLogs { get; set; } = true;
    public bool UseServiceRecovery { get; set; } = true;
    public int LogScanMaxDepth { get; set; } = 8;
    public int MaxLogsPerServiceForAnalysis { get; set; } = 32;
    public int LogTailLines { get; set; } = 2500;
    public int ZoneWarningPercent { get; set; } = 70;
    public int ZoneScaleRecommendPercent { get; set; } = 85;
    public int ZoneCriticalPercent { get; set; } = 95;
    public int ZonePrivateMemoryBudgetMb { get; set; } = 3072;
    // Adaptive Hook / Vertical Scaling profile
    public int HookWorldManagerClientSessions { get; set; } = 3000;
    public int HookWorldManagerZoneSessions { get; set; } = 150;
    public int HookZoneClientSessions { get; set; } = 1500;
    public int HookZoneShinePlayer { get; set; } = 2000;
    public int HookZoneShineMob { get; set; } = 12000;
    public int HookZoneShineNpc { get; set; } = 512;
    public int HookCpuWarnPercent { get; set; } = 70;
    public int HookCpuBlockPercent { get; set; } = 90;
    public int HookMemoryWarnPercent { get; set; } = 75;
    public int HookMemoryBlockPercent { get; set; } = 90;
    public bool AllowExperimentalZoneBinaryHooks { get; set; } = false;

    // Hardware-aware process scheduling. Off by default until explicitly enabled.
    public bool AutoApplyCpuAffinity { get; set; } = false;
}
