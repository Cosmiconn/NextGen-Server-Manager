using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class HealthEngine
{
    public void Evaluate(FiestaServiceEntry svc)
    {
        if (svc.State == ServiceRuntimeState.Missing)
        {
            svc.Health = HealthState.Failed;
            svc.StatusText = "Dienst fehlt";
            return;
        }
        if (svc.State != ServiceRuntimeState.Running)
        {
            svc.Health = HealthState.Failed;
            svc.StatusText = svc.State == ServiceRuntimeState.Stopped ? "Gestoppt" : svc.State.ToString();
            return;
        }
        if (svc.ClientPort is int && !svc.PortOpen)
        {
            svc.Health = HealthState.Degraded;
            svc.StatusText = "Läuft, Port nicht bereit";
            return;
        }
        if (svc.ClientPort is int && svc.PortOwnerPid is int owner && svc.ProcessId is int pid && owner != pid)
        {
            svc.Health = HealthState.Degraded;
            svc.StatusText = $"Port gehört PID {owner}";
            return;
        }
        svc.Health = HealthState.Healthy;
        svc.StatusText = "Online";
    }

    public int Score(IEnumerable<FiestaServiceEntry> services)
    {
        var list = services.ToList();
        if (list.Count == 0) return 0;
        double points = 0;
        foreach (var s in list)
            points += s.Health switch { HealthState.Healthy => 1, HealthState.Degraded => 0.5, _ => 0 };
        return (int)Math.Round(100 * points / list.Count);
    }
}
