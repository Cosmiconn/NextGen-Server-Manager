using System.Runtime.InteropServices;
using Microsoft.Win32;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Lightweight host-hardware advisor.  It intentionally separates per-process/core pressure
/// from total-machine CPU so a many-core host is not mistaken for having vertical headroom
/// when a Zone mainthread is already saturated.
/// </summary>
public sealed class HardwareAdvisorService
{
    private readonly SystemResourceMonitorService _resources = new();

    public HardwareProfileSnapshot Analyze(IReadOnlyList<FiestaServiceEntry> services)
    {
        var system = _resources.Sample();
        var zones = services.Where(x => x.Kind == FiestaServiceKind.Zone && x.State == ServiceRuntimeState.Running).ToList();
        var wm = services.FirstOrDefault(x => x.Kind == FiestaServiceKind.WorldManager && x.State == ServiceRuntimeState.Running);
        var maxZone = zones.Select(x => x.CpuCorePercent).DefaultIfEmpty(0).Max();
        var avgZone = zones.Select(x => x.CpuCorePercent).DefaultIfEmpty(0).Average();
        var model = ReadCpuModel();
        var (manufacturer, systemModel) = ReadSystemIdentity();
        var mhz = ReadNominalMhz();
        var numa = ReadNumaNodes();
        var logical = Environment.ProcessorCount;

        string mode;
        string advice;
        if (maxZone >= 90)
        {
            mode = "CPU/Mainthread-limitiert";
            advice = "Mindestens eine Zone liegt praktisch auf einem vollen logischen CPU-Kern. Größere Player/Mob-Pools bringen dort kaum Nutzen. Höherer Single-Core-Takt oder Lastverlagerung auf eine weitere Zone ist sinnvoller; ein zweiter CPU-Sockel erhöht vor allem die Zahl parallel betreibbarer Zonen.";
        }
        else if (maxZone >= 70 && system.CpuPercent < 65)
        {
            mode = "Single-Core limitiert, Host hat Parallelreserve";
            advice = "Die Maschine hat insgesamt noch CPU-Reserve, einzelne Zone-Mainthreads liegen aber bereits im Warnbereich. Für weniger Zonen ist höherer Pro-Kern-Takt wichtiger als zusätzliche Kerne. Ein zweiter Sockel hilft hauptsächlich für zusätzliche Zone-/DB-/Nebenprozesse.";
        }
        else if (system.MemoryUsedPercent >= 85)
        {
            mode = "RAM-limitiert";
            advice = "Physischer RAM ist knapp. Keine weiteren Poolvergrößerungen empfehlen, bevor Speicherreserve geschaffen wurde.";
        }
        else if (maxZone < 55 && system.MemoryUsedPercent < 75)
        {
            mode = "Vertikale Reserve";
            advice = "CPU-Core- und RAM-Reserve sind vorhanden. Kontrollierte Session-/Pool-Erhöhungen sind sinnvoll, sofern die jeweiligen Binary-/Handle-Abhängigkeiten verifiziert sind.";
        }
        else
        {
            mode = "Ausgewogen / beobachten";
            advice = "Moderate vertikale Skalierung ist möglich. Änderungen schrittweise durchführen und Zone-Core-CPU, Private RAM, Tick-/Logfehler sowie WM-Sessiondruck vergleichen.";
        }

        if (numa > 1)
            advice += " Mehrere NUMA-Nodes erkannt: WM und einzelne Zone-Prozesse möglichst node-lokal halten; Cross-NUMA-Migrationen vermeiden.";

        if (manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase)
            && systemModel.Contains("R720", StringComparison.OrdinalIgnoreCase))
        {
            if (!model.Contains("E5-2667 v2", StringComparison.OrdinalIgnoreCase))
                advice += " R720-Hinweis: Für Fiesta ist 2× Xeon E5-2667 v2 (8C, 3,3 GHz Basis, bis 4,0 GHz, 130 W) die bevorzugte High-Clock-Konfiguration; E5-2690 v2 ist der 10-Core-Kompromiss, E5-2697 v2 priorisiert Parallelität.";
            else if (numa <= 1)
                advice += " R720-Hinweis: E5-2667 v2 erkannt. Ein zweiter identischer E5-2667 v2 erhöht die Parallelreserve für weitere Zone-Prozesse deutlich.";
        }

        return new HardwareProfileSnapshot
        {
            CpuModel = model,
            SystemManufacturer = manufacturer,
            SystemModel = systemModel,
            LogicalProcessors = logical,
            NumaNodes = numa,
            NominalMhz = mhz,
            TotalMemoryMb = system.TotalMemoryMb,
            AvailableMemoryMb = system.AvailableMemoryMb,
            SystemCpuPercent = system.CpuPercent,
            HighestZoneCorePercent = maxZone,
            AverageRunningZoneCorePercent = avgZone,
            RunningZones = zones.Count,
            WorldManagerCorePercent = wm?.CpuCorePercent ?? 0,
            ScalingMode = mode,
            Advice = advice
        };
    }

    private static (string Manufacturer, string Model) ReadSystemIdentity()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var manufacturer = key?.GetValue("SystemManufacturer")?.ToString()?.Trim() ?? string.Empty;
            var model = key?.GetValue("SystemProductName")?.ToString()?.Trim() ?? string.Empty;
            return (manufacturer, model);
        }
        catch { return (string.Empty, string.Empty); }
    }

    private static string ReadCpuModel()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "unbekannt";
        }
        catch { return "unbekannt"; }
    }

    private static int ReadNominalMhz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var value = key?.GetValue("~MHz");
            return value is int i ? i : int.TryParse(value?.ToString(), out var n) ? n : 0;
        }
        catch { return 0; }
    }

    private static int ReadNumaNodes()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return 1;
        try
        {
            return GetNumaHighestNodeNumber(out var highest) ? checked((int)highest + 1) : 1;
        }
        catch { return 1; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumaHighestNodeNumber(out uint HighestNodeNumber);
}
