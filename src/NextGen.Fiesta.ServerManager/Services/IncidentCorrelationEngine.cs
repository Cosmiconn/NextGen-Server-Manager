using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class IncidentCorrelationEngine
{
    public IReadOnlyList<DiagnosticIssue> Correlate(
        IReadOnlyCollection<LogEvent> liveEvents,
        IReadOnlyCollection<RuntimeTransition> transitions,
        IReadOnlyCollection<FiestaServiceEntry> services)
    {
        var issues = new List<DiagnosticIssue>();
        var now = DateTime.Now;

        foreach (var zone in services.Where(x => x.Kind == FiestaServiceKind.Zone))
        {
            var zoneTransitions = transitions
                .Where(x => x.ServiceName.Equals(zone.ServiceName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Timestamp)
                .ToList();

            var recentStops = zoneTransitions.Count(x => x.Timestamp >= now.AddMinutes(-10)
                && x.CurrentState == ServiceRuntimeState.Stopped
                && x.PreviousState != ServiceRuntimeState.Stopped);
            if (recentStops >= 3)
            {
                issues.Add(new DiagnosticIssue
                {
                    Code = "NG-ZONE-0016",
                    Severity = DiagnosticSeverity.Critical,
                    Title = "Zone befindet sich in einer Crash-/Restart-Schleife",
                    Description = $"{zone.DisplayName} wurde in den letzten 10 Minuten {recentStops} Mal gestoppt oder ist beendet worden.",
                    Recommendation = "Automatische Neustarts vorübergehend begrenzen, den ersten Fehler unmittelbar vor jedem Stop vergleichen und anschließend Konfiguration/Mapdaten gezielt korrigieren.",
                    Source = "Runtime correlation",
                    ServiceName = zone.ServiceName,
                    ZoneNumber = zone.ZoneNumber,
                    Evidence = string.Join(Environment.NewLine, zoneTransitions.TakeLast(8).Select(x => $"{x.Timestamp:HH:mm:ss} {x.StateText} PID {x.PreviousPid}->{x.CurrentPid}")),
                    Confidence = 0.99
                });
            }

            var lastStop = zoneTransitions.LastOrDefault(x => x.CurrentState == ServiceRuntimeState.Stopped);
            var recovery = lastStop is null ? null : zoneTransitions.FirstOrDefault(x => x.Timestamp > lastStop.Timestamp
                && x.Timestamp <= lastStop.Timestamp.AddMinutes(3)
                && x.CurrentState == ServiceRuntimeState.Running);
            if (lastStop is not null && recovery is not null)
            {
                issues.Add(new DiagnosticIssue
                {
                    Code = "NG-RUNTIME-0002",
                    Severity = DiagnosticSeverity.Info,
                    Title = "Zone nach Ausfall erfolgreich wiederhergestellt",
                    Description = $"{zone.DisplayName} wechselte nach einem Stop innerhalb von {(recovery.Timestamp - lastStop.Timestamp).TotalSeconds:F0}s wieder auf RUNNING.",
                    Recommendation = "Wenn dies nur beim Booten vorkommt, Smart Start verwenden. Bei wiederholten Laufzeit-Ausfällen die Logzeilen direkt vor dem Stop untersuchen.",
                    Source = "Runtime correlation",
                    ServiceName = zone.ServiceName,
                    ZoneNumber = zone.ZoneNumber,
                    Evidence = $"STOP {lastStop.Timestamp:HH:mm:ss}; RUNNING {recovery.Timestamp:HH:mm:ss}",
                    Confidence = 0.92
                });
            }
        }

        foreach (var group in liveEvents.Where(x => x.Timestamp >= now.AddMinutes(-5) && x.Message.Contains("RECONNECT", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.OrderBy(x => x.Timestamp).ToList();
            var maxBurst = MaxEventsInWindow(list.Select(x => x.Timestamp), TimeSpan.FromSeconds(60));
            if (maxBurst >= 5)
            {
                issues.Add(new DiagnosticIssue
                {
                    Code = "NG-NET-0010",
                    Severity = DiagnosticSeverity.Error,
                    Title = "S2S-Reconnect-Burst erkannt",
                    Description = $"Mindestens {maxBurst} Reconnect-Meldungen innerhalb von 60 Sekunden wurden erkannt.",
                    Recommendation = "Zielservice, internen Port, Port-PID-Zuordnung und Startreihenfolge prüfen. Einzelne Reconnects beim Booten sind normal; ein Burst ist es nicht.",
                    Source = group.Key,
                    Evidence = string.Join(Environment.NewLine, list.TakeLast(10).Select(x => $"{x.Timestamp:HH:mm:ss} {x.Message}")),
                    Confidence = 0.96
                });
            }
        }

        var wm = services.FirstOrDefault(x => x.Kind == FiestaServiceKind.WorldManager);
        if (wm?.ProcessStartTime is DateTime wmStart)
        {
            var unknown = liveEvents.Where(x => x.Timestamp >= now.AddMinutes(-15)
                && (x.Message.Contains("SERVER_ID_UNKNOWN", StringComparison.OrdinalIgnoreCase)
                    || x.Message.Contains("Parser's unknown", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Timestamp)
                .ToList();

            foreach (var evt in unknown.TakeLast(5))
            {
                var age = evt.Timestamp - wmStart;
                var startup = age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(2);
                issues.Add(new DiagnosticIssue
                {
                    Code = startup ? "NG-ZONE-0017" : "NG-PROTO-0010",
                    Severity = startup ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                    Title = startup ? "Wahrscheinliches WM↔Zone-Startup-Race" : "Parser-/Sessionfehler im laufenden Betrieb",
                    Description = startup
                        ? $"Ein unbekannter Session-/Parserzustand trat {age.TotalSeconds:F0}s nach dem WorldManager-Start auf. Das passt zu einem zu frühen bzw. konkurrierenden Zone-Handshake."
                        : "Ein unbekannter Session-/Parserzustand trat deutlich nach der Startup-Phase auf und sollte nicht als normales Boot-Rauschen behandelt werden.",
                    Recommendation = startup
                        ? "Smart Start verwenden und die Zone erst nach WM-Readiness starten. Falls dieselbe Meldung danach nicht wiederkehrt, als Startup-Anomalie beobachten."
                        : "Betroffene Socket-Verbindung über Port/PID identifizieren, Client/Server-/Plugin-Versionen prüfen und den Opcode mit PDB-Symbolen auflösen.",
                    Source = evt.Source,
                    Evidence = $"WM start={wmStart:HH:mm:ss}; event={evt.Timestamp:HH:mm:ss}; {evt.Message}",
                    Confidence = startup ? 0.94 : 0.97,
                    RepairAction = startup ? RepairActionKind.SmartStart : RepairActionKind.None
                });
            }
        }

        return issues;
    }

    private static int MaxEventsInWindow(IEnumerable<DateTime> timestamps, TimeSpan window)
    {
        var values = timestamps.OrderBy(x => x).ToArray();
        var best = 0;
        var left = 0;
        for (var right = 0; right < values.Length; right++)
        {
            while (left <= right && values[right] - values[left] > window) left++;
            best = Math.Max(best, right - left + 1);
        }
        return best;
    }
}
