using System.Diagnostics;
using System.Runtime.InteropServices;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Builds and applies process-level CPU affinity from the real Windows physical-core/SMT/NUMA topology.
/// SMT siblings stay together as one physical scheduling unit.
/// </summary>
public sealed class ProcessorAffinityService
{
    private const int RelationProcessorCore = 0;

    public CpuAffinityPlan BuildPlan(IReadOnlyList<FiestaServiceEntry> services)
        => BuildPlan(ReadTopology(), services, readCurrentMasks: true);

    public CpuAffinityApplyResult ApplyPlan(IReadOnlyList<FiestaServiceEntry> services)
    {
        var plan = BuildPlan(services);
        if (!plan.Topology.Supported)
            return new CpuAffinityApplyResult(false, 0, plan, plan.Summary);

        var applied = 0;
        var errors = new List<string>();
        foreach (var entry in plan.Entries.Where(x => x.CanApply && x.ProcessId is > 0 && x.TargetMask != 0))
        {
            try
            {
                using var process = Process.GetProcessById(entry.ProcessId!.Value);
                var current = ToUInt64(process.ProcessorAffinity);
                if (current == entry.TargetMask)
                    continue;

                process.ProcessorAffinity = ToIntPtr(entry.TargetMask);
                applied++;
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.Component}: {ex.Message}");
            }
        }

        var refreshed = BuildPlan(services);
        var ok = errors.Count == 0;
        var detail = ok
            ? $"CPU-AFFINITY ANGEWENDET · {applied} Prozess(e) geändert · {refreshed.Summary}"
            : $"CPU-AFFINITY TEILWEISE · {applied} geändert · Fehler: {string.Join(" | ", errors.Take(4))}";
        return new CpuAffinityApplyResult(ok, applied, refreshed, detail);
    }

    public static CpuAffinityPlannerSelfTestResult RunPlannerSelfTest()
    {
        try
        {
            var cores = Enumerable.Range(0, 8)
                .Select(i => new CpuPhysicalCore(
                    i,
                    i < 4 ? 0 : 1,
                    (1UL << (i * 2)) | (1UL << (i * 2 + 1)),
                    new[] { i * 2, i * 2 + 1 }))
                .ToArray();
            var topology = new CpuTopologySnapshot(true, 16, 8, 2, cores, "synthetic");
            var services = new List<FiestaServiceEntry>
            {
                Synthetic("_WorldManager", FiestaServiceKind.WorldManager, 100),
                Synthetic("_Login", FiestaServiceKind.Login, 101),
                Synthetic("_Character", FiestaServiceKind.Character, 102),
                Synthetic("_Account", FiestaServiceKind.Account, 103),
                Synthetic("_Zone0", FiestaServiceKind.Zone, 104, 0)
            };

            var plan = BuildPlan(topology, services, readCurrentMasks: false);
            var zone = plan.Entries.Single(x => x.Kind == FiestaServiceKind.Zone);
            var wm = plan.Entries.Single(x => x.Kind == FiestaServiceKind.WorldManager);
            var login = plan.Entries.Single(x => x.Kind == FiestaServiceKind.Login);

            if (zone.TargetMask == 0 || wm.TargetMask == 0 || login.TargetMask == 0)
                throw new InvalidDataException("Planner erzeugte leere Masken.");
            if ((zone.TargetMask & wm.TargetMask) != 0 || (zone.TargetMask & login.TargetMask) != 0)
                throw new InvalidDataException("Testzone überschneidet sich mit WorldManager/Login.");
            if (CountPhysicalCores(zone.TargetMask, cores) < 2)
                throw new InvalidDataException("Einzelne laufende Testzone erhält trotz Reserve keine zwei physischen Cores.");
            foreach (var core in cores)
            {
                var intersection = zone.TargetMask & core.Mask;
                if (intersection != 0 && intersection != core.Mask)
                    throw new InvalidDataException("SMT-Geschwister eines physischen Cores wurden getrennt.");
            }

            return new CpuAffinityPlannerSelfTestResult(
                true,
                "CPU AFFINITY SELFTEST: PASS · physische Cores/SMT bleiben zusammen · Zone isoliert von World/Login · Einzelzone erhält ≥2 physische Cores.");
        }
        catch (Exception ex)
        {
            return new CpuAffinityPlannerSelfTestResult(false, "CPU AFFINITY SELFTEST: FAIL · " + ex.Message);
        }
    }

    private static FiestaServiceEntry Synthetic(string name, FiestaServiceKind kind, int pid, int? zone = null)
        => new()
        {
            DisplayName = name,
            ServiceName = name,
            Kind = kind,
            ZoneNumber = zone,
            DirectoryPath = ".",
            ExecutablePath = ".",
            ProcessId = pid,
            State = ServiceRuntimeState.Running
        };

    private static CpuAffinityPlan BuildPlan(
        CpuTopologySnapshot topology,
        IReadOnlyList<FiestaServiceEntry> services,
        bool readCurrentMasks)
    {
        if (!topology.Supported || topology.PhysicalCores.Count == 0)
        {
            return new CpuAffinityPlan(
                topology,
                Array.Empty<ProcessAffinityPlanEntry>(),
                "CPU-Affinity nicht anwendbar: " + topology.Detail);
        }

        var cores = topology.PhysicalCores.OrderBy(x => x.NumaNode).ThenBy(x => x.Index).ToList();
        var reserveCore = cores[0];
        var available = cores.Skip(1).ToList();
        var assigned = new Dictionary<FiestaServiceEntry, List<CpuPhysicalCore>>();

        CpuPhysicalCore TakeCore(int? preferredNode = null)
        {
            CpuPhysicalCore? selected = null;
            if (preferredNode.HasValue)
                selected = available.FirstOrDefault(x => x.NumaNode == preferredNode.Value);
            selected ??= available.FirstOrDefault();

            if (selected is null)
                return cores.Count > 1 ? cores[1] : reserveCore;

            available.Remove(selected);
            return selected;
        }

        var running = services
            .Where(x => x.State == ServiceRuntimeState.Running && x.ProcessId is > 0)
            .ToList();

        var world = running.FirstOrDefault(x => x.Kind == FiestaServiceKind.WorldManager);
        if (world is not null)
            assigned[world] = new List<CpuPhysicalCore> { TakeCore(cores[0].NumaNode) };

        var login = running.FirstOrDefault(x => x.Kind == FiestaServiceKind.Login);
        if (login is not null)
            assigned[login] = new List<CpuPhysicalCore> { TakeCore(topology.NumaNodes > 1 ? 1 : null) };

        var character = running.FirstOrDefault(x => x.Kind == FiestaServiceKind.Character);
        if (character is not null)
            assigned[character] = new List<CpuPhysicalCore> { TakeCore(login is null ? null : assigned[login][0].NumaNode) };

        var account = running.FirstOrDefault(x => x.Kind == FiestaServiceKind.Account);
        if (account is not null)
            assigned[account] = new List<CpuPhysicalCore> { TakeCore() };

        foreach (var background in running.Where(x =>
                     x.Kind is FiestaServiceKind.AccountLog
                         or FiestaServiceKind.GameLog
                         or FiestaServiceKind.GamigoZR))
        {
            assigned[background] = new List<CpuPhysicalCore> { reserveCore };
        }

        var zones = running
            .Where(x => x.Kind == FiestaServiceKind.Zone)
            .OrderBy(x => x.ZoneNumber ?? int.MaxValue)
            .ToList();

        if (zones.Count > 0)
        {
            var zonePool = available.Count > 0
                ? available.ToList()
                : cores.Where(x => x != reserveCore).ToList();
            if (zonePool.Count == 0)
                zonePool.Add(reserveCore);

            var coresPerZone = zonePool.Count >= zones.Count * 2 ? 2 : 1;
            if (zones.Count == 1 && zonePool.Count >= 2)
                coresPerZone = 2;

            var cursor = 0;
            foreach (var zone in zones)
            {
                var selected = new List<CpuPhysicalCore>();
                if (zonePool.Count >= coresPerZone)
                {
                    var node = zonePool
                        .GroupBy(x => x.NumaNode)
                        .OrderByDescending(g => g.Count())
                        .ThenBy(g => g.Key)
                        .First()
                        .Key;
                    selected.AddRange(zonePool.Where(x => x.NumaNode == node).Take(coresPerZone));
                    if (selected.Count < coresPerZone)
                        selected.AddRange(zonePool.Where(x => !selected.Contains(x)).Take(coresPerZone - selected.Count));
                    foreach (var core in selected) zonePool.Remove(core);
                }
                else
                {
                    selected.Add(zonePool[cursor++ % zonePool.Count]);
                }
                assigned[zone] = selected;
            }
        }

        foreach (var other in running.Where(x => !assigned.ContainsKey(x)))
            assigned[other] = new List<CpuPhysicalCore> { reserveCore };

        var maskUseCount = assigned
            .SelectMany(x => x.Value.Select(core => (Service: x.Key, Core: core)))
            .GroupBy(x => x.Core.Index)
            .ToDictionary(g => g.Key, g => g.Count());

        var entries = new List<ProcessAffinityPlanEntry>();
        foreach (var service in services.OrderBy(ServiceSortKey).ThenBy(x => x.ZoneNumber ?? -1))
        {
            if (service.State != ServiceRuntimeState.Running || service.ProcessId is not > 0)
            {
                entries.Add(new ProcessAffinityPlanEntry
                {
                    Component = service.DisplayName,
                    ServiceName = service.ServiceName,
                    Kind = service.Kind,
                    ProcessId = service.ProcessId,
                    TargetMask = 0,
                    CpuSet = "dynamisch beim Start",
                    Numa = "-",
                    CurrentAffinity = "-",
                    TargetAffinity = "-",
                    Status = "gestoppt · Auto-Apply plant beim nächsten Refresh",
                    CanApply = false
                });
                continue;
            }

            var selected = assigned[service];
            var targetMask = selected.Aggregate(0UL, (mask, core) => mask | core.Mask);
            var shared = selected.Any(core => maskUseCount.TryGetValue(core.Index, out var count) && count > 1);
            var current = readCurrentMasks ? TryReadCurrentMask(service.ProcessId.Value) : null;
            entries.Add(new ProcessAffinityPlanEntry
            {
                Component = service.DisplayName,
                ServiceName = service.ServiceName,
                Kind = service.Kind,
                ProcessId = service.ProcessId,
                TargetMask = targetMask,
                CpuSet = FormatCpuSet(targetMask, topology.LogicalProcessors),
                Numa = string.Join(",", selected.Select(x => x.NumaNode).Distinct().OrderBy(x => x)),
                CurrentAffinity = current.HasValue ? FormatCpuSet(current.Value, topology.LogicalProcessors) : "?",
                TargetAffinity = $"0x{targetMask:X}",
                Status = shared ? "geteilt (zu wenig physische Cores)" : "isoliert / node-lokal",
                CanApply = targetMask != 0
            });
        }

        var summary =
            $"CPU-Affinity-Plan · {topology.PhysicalCores.Count} physische Cores / {topology.LogicalProcessors} logische CPUs / {topology.NumaNodes} NUMA · " +
            $"{zones.Count} laufende Zone(n) · SMT-Geschwister bleiben zusammen · Prozess-Affinity, keine Binary-Änderung.";
        return new CpuAffinityPlan(topology, entries, summary);
    }

    private static int ServiceSortKey(FiestaServiceEntry service) => service.Kind switch
    {
        FiestaServiceKind.WorldManager => 0,
        FiestaServiceKind.Login => 1,
        FiestaServiceKind.Character => 2,
        FiestaServiceKind.Account => 3,
        FiestaServiceKind.Zone => 4,
        _ => 5
    };

    private static int CountPhysicalCores(ulong mask, IReadOnlyList<CpuPhysicalCore> cores)
        => cores.Count(core => (mask & core.Mask) == core.Mask);

    private static ulong? TryReadCurrentMask(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return ToUInt64(process.ProcessorAffinity);
        }
        catch { return null; }
    }

    private static string FormatCpuSet(ulong mask, int logicalCount)
    {
        if (mask == 0) return "-";
        var ids = new List<int>();
        for (var i = 0; i < Math.Min(64, logicalCount); i++)
            if ((mask & (1UL << i)) != 0) ids.Add(i);
        return "CPU " + string.Join(",", ids);
    }

    private static ulong ToUInt64(IntPtr value)
        => IntPtr.Size == 8
            ? unchecked((ulong)value.ToInt64())
            : unchecked((uint)value.ToInt32());

    private static IntPtr ToIntPtr(ulong value)
        => IntPtr.Size == 8
            ? new IntPtr(unchecked((long)value))
            : new IntPtr(unchecked((int)value));

    private static CpuTopologySnapshot ReadTopology()
    {
        if (!OperatingSystem.IsWindows())
            return new CpuTopologySnapshot(false, Environment.ProcessorCount, 0, 1, Array.Empty<CpuPhysicalCore>(), "Nur Windows wird unterstützt.");

        if (Environment.ProcessorCount > 64)
        {
            return new CpuTopologySnapshot(
                false,
                Environment.ProcessorCount,
                0,
                1,
                Array.Empty<CpuPhysicalCore>(),
                "Mehr als 64 logische Prozessoren/Processor Groups: sichere gruppenübergreifende Affinity wird noch nicht angewendet.");
        }

        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0)
        {
            return new CpuTopologySnapshot(false, Environment.ProcessorCount, 0, 1, Array.Empty<CpuPhysicalCore>(),
                $"GetLogicalProcessorInformationEx lieferte keine Topologie (Win32 {Marshal.GetLastWin32Error()}).");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                return new CpuTopologySnapshot(false, Environment.ProcessorCount, 0, 1, Array.Empty<CpuPhysicalCore>(),
                    $"CPU-Topologie konnte nicht gelesen werden (Win32 {Marshal.GetLastWin32Error()}).");
            }

            var cores = new List<CpuPhysicalCore>();
            var offset = 0;
            var multiGroup = false;
            while (offset < length)
            {
                var item = IntPtr.Add(buffer, offset);
                var relationship = Marshal.ReadInt32(item, 0);
                var size = Marshal.ReadInt32(item, 4);
                if (size <= 0) break;

                if (relationship == RelationProcessorCore && size >= 32)
                {
                    var groupCount = unchecked((ushort)Marshal.ReadInt16(item, 30));
                    ulong mask = 0;
                    ushort group = 0;
                    for (var g = 0; g < groupCount; g++)
                    {
                        var groupAffinityOffset = 32 + g * (IntPtr.Size + 8);
                        var groupMask = IntPtr.Size == 8
                            ? unchecked((ulong)Marshal.ReadInt64(item, groupAffinityOffset))
                            : unchecked((uint)Marshal.ReadInt32(item, groupAffinityOffset));
                        var thisGroup = unchecked((ushort)Marshal.ReadInt16(item, groupAffinityOffset + IntPtr.Size));
                        if (thisGroup != 0)
                        {
                            multiGroup = true;
                            continue;
                        }
                        group = thisGroup;
                        mask |= groupMask;
                    }

                    if (mask != 0 && group == 0)
                    {
                        var logical = new List<int>();
                        for (var bit = 0; bit < 64; bit++)
                            if ((mask & (1UL << bit)) != 0) logical.Add(bit);

                        var node = 0;
                        if (logical.Count > 0)
                        {
                            var pn = new ProcessorNumber { Group = 0, Number = checked((byte)logical[0]) };
                            if (GetNumaProcessorNodeEx(ref pn, out var numaNode) && numaNode != ushort.MaxValue)
                                node = numaNode;
                        }
                        cores.Add(new CpuPhysicalCore(cores.Count, node, mask, logical));
                    }
                }

                offset += size;
            }

            if (multiGroup)
            {
                return new CpuTopologySnapshot(false, Environment.ProcessorCount, cores.Count, cores.Select(x => x.NumaNode).Distinct().Count(), cores,
                    "Mehrere Windows Processor Groups erkannt; automatische Affinity bleibt aus Sicherheitsgründen gesperrt.");
            }

            var numa = Math.Max(1, cores.Select(x => x.NumaNode).Distinct().Count());
            return new CpuTopologySnapshot(
                cores.Count > 0,
                Environment.ProcessorCount,
                cores.Count,
                numa,
                cores,
                cores.Count > 0 ? "Windows physical-core topology" : "Keine physischen Cores erkannt.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int RelationshipType,
        IntPtr Buffer,
        ref uint ReturnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumaProcessorNodeEx(
        ref ProcessorNumber Processor,
        out ushort NodeNumber);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
    }
}

public sealed record CpuPhysicalCore(
    int Index,
    int NumaNode,
    ulong Mask,
    IReadOnlyList<int> LogicalProcessors);

public sealed record CpuTopologySnapshot(
    bool Supported,
    int LogicalProcessors,
    int PhysicalCoreCount,
    int NumaNodes,
    IReadOnlyList<CpuPhysicalCore> PhysicalCores,
    string Detail);

public sealed class ProcessAffinityPlanEntry
{
    public string Component { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public FiestaServiceKind Kind { get; init; }
    public int? ProcessId { get; init; }
    public ulong TargetMask { get; init; }
    public string CpuSet { get; init; } = string.Empty;
    public string Numa { get; init; } = string.Empty;
    public string CurrentAffinity { get; init; } = string.Empty;
    public string TargetAffinity { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool CanApply { get; init; }
}

public sealed record CpuAffinityPlan(
    CpuTopologySnapshot Topology,
    IReadOnlyList<ProcessAffinityPlanEntry> Entries,
    string Summary);

public sealed record CpuAffinityApplyResult(
    bool Success,
    int AppliedCount,
    CpuAffinityPlan Plan,
    string Detail);

public readonly record struct CpuAffinityPlannerSelfTestResult(bool Success, string Detail);
