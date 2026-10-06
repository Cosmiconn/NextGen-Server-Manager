using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class RuntimeHistoryService
{
    private sealed record LastObserved(ServiceRuntimeState State, int? Pid, bool PortOpen, DateTime Timestamp);
    private readonly Dictionary<string, LastObserved> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RuntimeTransition> _transitions = new();
    private readonly object _gate = new();

    public RuntimeTransition? Observe(FiestaServiceEntry service, DateTime now)
    {
        lock (_gate)
        {
            var current = new LastObserved(service.State, service.ProcessId, service.PortOpen, now);
            if (!_last.TryGetValue(service.ServiceName, out var previous))
            {
                _last[service.ServiceName] = current;
                return null;
            }

            _last[service.ServiceName] = current;
            var stateChanged = previous.State != current.State;
            var pidChanged = previous.Pid != current.Pid && (previous.Pid is not null || current.Pid is not null);
            if (!stateChanged && !pidChanged) return null;

            var reason = stateChanged
                ? $"Dienstzustand wechselte von {previous.State} auf {current.State}."
                : $"Prozess-ID wechselte von {previous.Pid?.ToString() ?? "-"} auf {current.Pid?.ToString() ?? "-"}.";

            var transition = new RuntimeTransition(
                now,
                service.ServiceName,
                service.DisplayName,
                service.ZoneNumber,
                previous.State,
                current.State,
                previous.Pid,
                current.Pid,
                current.PortOpen,
                reason);

            _transitions.Add(transition);
            if (_transitions.Count > 1000) _transitions.RemoveRange(0, _transitions.Count - 1000);
            return transition;
        }
    }

    public IReadOnlyList<RuntimeTransition> Snapshot(TimeSpan? maxAge = null)
    {
        lock (_gate)
        {
            IEnumerable<RuntimeTransition> query = _transitions;
            if (maxAge is TimeSpan age)
            {
                var cutoff = DateTime.Now - age;
                query = query.Where(x => x.Timestamp >= cutoff);
            }
            return query.ToList();
        }
    }

    public int CountStops(string serviceName, TimeSpan window)
    {
        var cutoff = DateTime.Now - window;
        lock (_gate)
            return _transitions.Count(x => x.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase)
                && x.Timestamp >= cutoff
                && x.CurrentState == ServiceRuntimeState.Stopped
                && x.PreviousState != ServiceRuntimeState.Stopped);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _last.Clear();
            _transitions.Clear();
        }
    }
}
