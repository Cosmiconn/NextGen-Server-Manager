namespace NextGen.Fiesta.ServerManager.Models;

public sealed class ZoneProvisionPlan
{
    public int ZoneNumber { get; init; }
    public int SourceZoneNumber { get; init; }
    public string SourceDirectory { get; init; } = string.Empty;
    public string TargetDirectory { get; init; } = string.Empty;
    public int ClientPort { get; init; }
    public int InternalPort { get; init; }
    public int OptoolPort { get; init; }
    public string FirewallRuleName { get; init; } = string.Empty;
    public bool PortsAvailable { get; init; }
    public bool TargetAvailable { get; init; }
    public bool ServiceAvailable { get; init; }
    public string Detail { get; init; } = string.Empty;

    public bool IsValid => PortsAvailable && TargetAvailable && ServiceAvailable && Directory.Exists(SourceDirectory);
    public string ZoneName => $"Zone{ZoneNumber:00}";
    public string SourceZoneName => $"Zone{SourceZoneNumber:00}";
    public string PortsText => $"Client {ClientPort} · intern {InternalPort} · OPTool {OptoolPort}";
    public string FirewallText => $"TCP {ClientPort} eingehend (nur Client-Port)";
    public string ValidText => IsValid ? "Bereit" : "Nicht bereit";
}
