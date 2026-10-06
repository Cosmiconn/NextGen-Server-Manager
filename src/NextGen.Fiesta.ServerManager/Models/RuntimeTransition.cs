namespace NextGen.Fiesta.ServerManager.Models;

public sealed record RuntimeTransition(
    DateTime Timestamp,
    string ServiceName,
    string DisplayName,
    int? ZoneNumber,
    ServiceRuntimeState PreviousState,
    ServiceRuntimeState CurrentState,
    int? PreviousPid,
    int? CurrentPid,
    bool PortOpen,
    string Reason)
{
    public string StateText => $"{PreviousState} → {CurrentState}";
    public string TimeText => Timestamp.ToString("HH:mm:ss");
}
