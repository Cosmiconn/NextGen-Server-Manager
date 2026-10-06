namespace NextGen.Fiesta.ServerManager.Models;

public enum FiestaServiceKind
{
    Account,
    AccountLog,
    Login,
    Character,
    GameLog,
    WorldManager,
    GamigoZR,
    Zone,
    Unknown
}

public enum ServiceRuntimeState
{
    Unknown,
    Missing,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Paused
}

public enum HealthState
{
    Unknown,
    Healthy,
    Degraded,
    Failed
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public enum RepairActionKind
{
    None,
    StartService,
    RestartService,
    ReinstallService,
    SmartStart,
    FixZoneServerInfo,
    ConfigureRecovery
}
