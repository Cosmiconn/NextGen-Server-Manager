using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Adaptive, reversible capacity hooks for the NA2016 baseline.
/// Safe hooks are configuration-backed and/or a hash-gated WM runtime limit write.
/// Zone object-pool binary hooks remain intentionally blocked until every dependent
/// 16-bit handle range and downstream hard-coded bound has been verified.
/// </summary>
public sealed class AdaptiveHookService
{
    public const string BaselineWorldManagerSha256 = "23e94c78840a80f874adffa15792ae29f5d25df950418b3633f6e5f5ba68ede1";
    public const string BaselineZoneSha256 = "db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5";

    // Exact NA2016 WorldManager.exe addresses are represented as RVAs so ASLR is safe.
    // VA stock: g_UserLimit=0x00556BAC, CWMClientSessionManager=0x00556BB0,
    // m_MaxSessions=+0x08, m_NumSessions=+0x0C. Preferred image base is 0x00400000.
    private const long WmUserLimitRva = 0x00156BAC;
    private const long WmMaxSessionsRva = 0x00156BB8;
    private const long WmNumSessionsRva = 0x00156BBC;
    private const int WmClientSessionStride = 0x1F7B8;

    private readonly ServerInfoParser _parser = new();
    private readonly SystemResourceMonitorService _systemResources = new();

    private static readonly Regex ServerInfoLineRx = new(
        "^(?<prefix>\\s*SERVER_INFO\\s+\"(?<name>[^\"]+)\"\\s*,\\s*-?\\d+\\s*,\\s*-?\\d+\\s*,\\s*-?\\d+\\s*,\\s*(?<kind>-?\\d+)\\s*,\\s*\"[^\"]+\"\\s*,\\s*\\d+\\s*,\\s*\\d+\\s*,\\s*)(?<max>\\d+)(?<suffix>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AdaptiveHookAuditResult Analyze(string serverRoot, IReadOnlyList<FiestaServiceEntry> services, AppSettings settings)
    {
        var assessments = new List<AdaptiveHookAssessment>();
        var system = _systemResources.Sample();
        var serverInfoPath = Path.Combine(serverRoot, "9Data", "ServerInfo", "ServerInfo.txt");
        var entries = _parser.Parse(serverInfoPath);
        var wm = services.FirstOrDefault(x => x.Kind == FiestaServiceKind.WorldManager);
        var zones = services.Where(x => x.Kind == FiestaServiceKind.Zone).ToList();

        var wmClient = entries.FirstOrDefault(IsWorldManagerClientEntry);
        var wmZone = entries.FirstOrDefault(IsWorldManagerZoneEntry);
        var zoneClientLimits = entries.Where(x => x.ServerType == 6 && x.ConnectionKind == 20).Select(x => x.MaxAccept).ToArray();

        var wmHashOk = wm is not null && File.Exists(wm.ExecutablePath) && HashEquals(wm.ExecutablePath, BaselineWorldManagerSha256);
        var zoneHashOk = zones.Where(x => File.Exists(x.ExecutablePath)).All(x => HashEquals(x.ExecutablePath, BaselineZoneSha256));

        var runtime = wm is not null ? TryReadWorldManagerRuntime(wm) : new WmRuntimeState(false, 0, 0, 0, "WM läuft nicht / Runtimewerte nicht verfügbar");
        var currentWmHard = runtime.Success && runtime.MaxSessions > 0 ? runtime.MaxSessions : wmClient?.MaxAccept ?? 1500;
        var currentWmZone = wmZone?.MaxAccept ?? 100;
        var currentZoneClient = zoneClientLimits.Length > 0 ? zoneClientLimits.Max() : 1500;

        assessments.Add(AssessWorldManagerClient(wm, wmHashOk, runtime, currentWmHard, settings, system));
        assessments.Add(AssessSimpleConfig(
            "WorldManager", "Zone/S2S Sessions", "CONFIG-HOOK", currentWmZone, settings.HookWorldManagerZoneSessions,
            wm?.CpuCorePercent ?? 0, wm?.PrivateMemoryMb ?? 0, 0, settings, system,
            wmHashOk ? "Dynamischer WM-Sessionmanager; erhöht interne Zone/WM-Verbindungen, nicht die Spielerleistung." : "WM-Baseline-Hash passt nicht – keine adress-/buildabhängige Freigabe.",
            hashGate: wmHashOk));

        var maxZoneCpu = zones.Where(x => x.State == ServiceRuntimeState.Running).Select(x => x.CpuCorePercent).DefaultIfEmpty(0).Max();
        var maxZonePrivate = zones.Where(x => x.State == ServiceRuntimeState.Running).Select(x => x.PrivateMemoryMb).DefaultIfEmpty(0).Max();
        var zoneClientTarget = settings.HookZoneClientSessions;
        var zoneClientReason = zoneClientTarget > 1500
            ? "Der Socketwert darf ohne ShinePlayer-Hook nicht über 1.500 liegen. Sonst nimmt der Listener mehr Verbindungen an als der verifizierte Player-Objektpool tragen kann."
            : "Sicherer Config-Hook innerhalb des verifizierten ShinePlayer-Hardlimits (1.500).";
        assessments.Add(AssessSimpleConfig(
            "Zone", "Client nMaxAccept", "CONFIG-HOOK", currentZoneClient, zoneClientTarget,
            maxZoneCpu, maxZonePrivate, 0, settings, system, zoneClientReason,
            hashGate: zoneHashOk && zoneClientTarget <= 1500));

        assessments.Add(AssessZoneBinaryPool("ShinePlayer", 1500, settings.HookZoneShinePlayer, 0x2C058, maxZoneCpu, maxZonePrivate, settings, system, zoneHashOk,
            "Player-Hook verändert den 16-Bit-Objekthandle-Bereich. Neben der Pool-Allokation existieren abhängige Range-/Base-Konstanten; daher noch nicht automatisch freigegeben."));
        assessments.Add(AssessZoneBinaryPool("ShineMob", 8000, settings.HookZoneShineMob, 0x2568, maxZoneCpu, maxZonePrivate, settings, system, zoneHashOk,
            "Mob-Hook verschiebt die Basis aller nachfolgenden 16-Bit-Objekthandles. Ein isoliertes Ersetzen von 8.000 wäre nicht sicher."));
        assessments.Add(AssessZoneBinaryPool("ShineNPC", 256, settings.HookZoneShineNpc, 0x256C, maxZoneCpu, maxZonePrivate, settings, system, zoneHashOk,
            "NPC-Pool ist klein, aber Teil desselben globalen Handle-Layouts. Der Patch bleibt bis zur vollständigen Rebase-Matrix gesperrt."));

        var applyable = assessments.Count(x => x.CanApply);
        var blocks = assessments.Count(x => x.Decision == "BLOCKIERT");
        var warns = assessments.Count(x => x.Decision == "WARNUNG");
        var runtimeText = runtime.Success
            ? $"WM Runtime: {runtime.NumSessions:N0}/{runtime.MaxSessions:N0}, UserLimit {runtime.UserLimit:N0}."
            : "WM Runtime konnte noch nicht gelesen werden.";
        var summary = $"Adaptive Hooks: {applyable} direkt anwendbar · {warns} Warnung(en) · {blocks} gesperrt. {system.Summary}. {runtimeText} " +
                      "Safe Hooks ändern ServerInfo transaktional; der WM-Admission-Limit kann beim exakten Baseline-Build zusätzlich live gesetzt werden.";

        return new AdaptiveHookAuditResult { Assessments = assessments, Summary = summary };
    }

    public async Task<AdaptiveHookApplyResult> ApplySafeHooksAsync(
        string serverRoot,
        IReadOnlyList<FiestaServiceEntry> services,
        AppSettings settings,
        CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var audit = Analyze(serverRoot, services, settings);
        var safe = audit.Assessments.Where(x => x.CanApply && (x.HookType == "CONFIG-HOOK" || x.HookType == "CONFIG + LIVE WM")).ToList();
        if (safe.Count == 0)
            return new AdaptiveHookApplyResult(false, "Kein freigegebener Hook kann mit den aktuellen Zielwerten/Lastwerten angewendet werden.", string.Empty, false, false);

        var serverInfoPath = Path.Combine(serverRoot, "9Data", "ServerInfo", "ServerInfo.txt");
        if (!File.Exists(serverInfoPath))
            return new AdaptiveHookApplyResult(false, "ServerInfo.txt wurde nicht gefunden.", string.Empty, false, false);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(serverRoot, ".nextgen-backups", "adaptive-hooks", stamp);
        Directory.CreateDirectory(backupDir);
        File.Copy(serverInfoPath, Path.Combine(backupDir, "ServerInfo.txt"), true);

        var beforeText = File.ReadAllText(serverInfoPath, Encoding.Latin1);
        var changed = new List<string>();
        try
        {
            var lines = File.ReadAllLines(serverInfoPath, Encoding.Latin1).ToList();

            if (safe.Any(x => x.Scope == "WorldManager" && x.Resource == "Client Sessions"))
            {
                var count = RewriteMaxAccept(lines, IsWorldManagerClientLine, settings.HookWorldManagerClientSessions);
                if (count == 0) throw new InvalidDataException("WM-Client-SERVER_INFO-Zeile konnte nicht eindeutig gefunden werden.");
                changed.Add($"WM Client Sessions → {settings.HookWorldManagerClientSessions:N0}");
            }
            if (safe.Any(x => x.Scope == "WorldManager" && x.Resource == "Zone/S2S Sessions"))
            {
                var count = RewriteMaxAccept(lines, IsWorldManagerZoneLine, settings.HookWorldManagerZoneSessions);
                if (count == 0) throw new InvalidDataException("WM-Zone/S2S-SERVER_INFO-Zeile konnte nicht eindeutig gefunden werden.");
                changed.Add($"WM Zone/S2S → {settings.HookWorldManagerZoneSessions:N0}");
            }
            if (safe.Any(x => x.Scope == "Zone" && x.Resource == "Client nMaxAccept"))
            {
                if (settings.HookZoneClientSessions > 1500)
                    throw new InvalidOperationException("Zone nMaxAccept > 1.500 ist ohne verifizierten ShinePlayer-Binary-Hook gesperrt.");
                var count = RewriteMaxAccept(lines, IsZoneClientLine, settings.HookZoneClientSessions);
                if (count == 0) throw new InvalidDataException("Keine Zone-Client-SERVER_INFO-Zeilen gefunden.");
                changed.Add($"Zone Client nMaxAccept → {settings.HookZoneClientSessions:N0} ({count} Zeilen)");
            }

            File.WriteAllLines(serverInfoPath, lines, Encoding.Latin1);

            var wm = services.FirstOrDefault(x => x.Kind == FiestaServiceKind.WorldManager);
            var runtimeApplied = false;
            var restartRequired = false;
            var runtimeDetail = string.Empty;
            if (wm is not null && wm.State == ServiceRuntimeState.Running && wm.ProcessId.HasValue)
            {
                var state = TryReadWorldManagerRuntime(wm);
                if (state.Success && settings.HookWorldManagerClientSessions <= state.MaxSessions)
                {
                    var wr = TrySetWorldManagerUserLimit(wm, settings.HookWorldManagerClientSessions);
                    runtimeApplied = wr.Success;
                    runtimeDetail = wr.Detail;
                }
                else
                {
                    restartRequired = settings.HookWorldManagerClientSessions != state.MaxSessions;
                    runtimeDetail = restartRequired
                        ? $"WM muss neu gestartet werden, damit InitSessions({settings.HookWorldManagerClientSessions}) den neuen Hard-Pool anlegt."
                        : state.Detail;
                }
            }
            else
            {
                restartRequired = true;
                runtimeDetail = "WM läuft nicht; der neue Wert wird beim nächsten Start verwendet.";
            }

            // Any ServerInfo hard-cap change is startup-sensitive even if the live admission cap could be written.
            var parsedBefore = _parser.Parse(Path.Combine(backupDir, "ServerInfo.txt"));
            var oldWmHard = parsedBefore.FirstOrDefault(IsWorldManagerClientEntry)?.MaxAccept ?? 1500;
            var oldWmZone = parsedBefore.FirstOrDefault(IsWorldManagerZoneEntry)?.MaxAccept ?? 100;
            var oldZoneClient = parsedBefore.Where(x => x.ServerType == 6 && x.ConnectionKind == 20).Select(x => x.MaxAccept).DefaultIfEmpty(1500).Max();
            if (oldWmHard != settings.HookWorldManagerClientSessions
                || oldWmZone != settings.HookWorldManagerZoneSessions
                || oldZoneClient != settings.HookZoneClientSessions)
                restartRequired = true;

            var manifest = new
            {
                version = "0.3.3",
                createdAt = DateTimeOffset.Now,
                baseline = new { worldManagerSha256 = BaselineWorldManagerSha256, zoneSha256 = BaselineZoneSha256 },
                settings = new
                {
                    settings.HookWorldManagerClientSessions,
                    settings.HookWorldManagerZoneSessions,
                    settings.HookZoneClientSessions,
                    settings.HookZoneShinePlayer,
                    settings.HookZoneShineMob,
                    settings.HookZoneShineNpc,
                    settings.AllowExperimentalZoneBinaryHooks
                },
                applied = changed,
                runtimeWorldManagerLimitApplied = runtimeApplied,
                restartRequired,
                note = "Zone hard-pool binary hooks are intentionally not applied until the complete 16-bit object-handle rebase dependency graph is verified."
            };
            var hookDir = Path.Combine(serverRoot, ".nextgen-hooks");
            Directory.CreateDirectory(hookDir);
            File.WriteAllText(Path.Combine(hookDir, "adaptive-profile.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(backupDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            return new AdaptiveHookApplyResult(true,
                $"Hook-Profil angewendet: {string.Join(" · ", changed)}. {runtimeDetail}" + (restartRequired ? " Restart erforderlich." : string.Empty),
                backupDir, restartRequired, runtimeApplied);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(serverInfoPath, beforeText, Encoding.Latin1); } catch { }
            return new AdaptiveHookApplyResult(false, "Hook-Anwendung wurde zurückgerollt: " + ex.Message, backupDir, false, false);
        }
    }

    public AdaptiveHookApplyResult RestoreLatest(string serverRoot)
    {
        try
        {
            var root = Path.Combine(serverRoot, ".nextgen-backups", "adaptive-hooks");
            if (!Directory.Exists(root)) return new AdaptiveHookApplyResult(false, "Kein Adaptive-Hook-Backup vorhanden.", string.Empty, false, false);
            var latest = Directory.EnumerateDirectories(root).OrderByDescending(x => x).FirstOrDefault();
            if (latest is null) return new AdaptiveHookApplyResult(false, "Kein Adaptive-Hook-Backup vorhanden.", string.Empty, false, false);
            var backup = Path.Combine(latest, "ServerInfo.txt");
            var target = Path.Combine(serverRoot, "9Data", "ServerInfo", "ServerInfo.txt");
            if (!File.Exists(backup)) return new AdaptiveHookApplyResult(false, "Backup enthält keine ServerInfo.txt.", latest, false, false);
            File.Copy(backup, target, true);
            return new AdaptiveHookApplyResult(true, "Letzte Hook-Konfiguration wiederhergestellt. Serverkomponenten müssen neu gestartet werden, damit alle Hard-Pools sicher wieder zum Backup passen.", latest, true, false);
        }
        catch (Exception ex)
        {
            return new AdaptiveHookApplyResult(false, "Wiederherstellung fehlgeschlagen: " + ex.Message, string.Empty, false, false);
        }
    }

    private AdaptiveHookAssessment AssessWorldManagerClient(FiestaServiceEntry? wm, bool hashOk, WmRuntimeState runtime, int currentLimit, AppSettings settings, SystemResourceSnapshot system)
    {
        var target = Math.Max(1, settings.HookWorldManagerClientSessions);
        var delta = Math.Max(0, target - currentLimit);
        var extraMb = delta * (double)WmClientSessionStride / 1024d / 1024d;
        var targetPoolMb = target * (double)WmClientSessionStride / 1024d / 1024d;
        var currentMem = wm?.PrivateMemoryMb ?? 0;
        var projected = currentMem > 0 ? currentMem + extraMb : targetPoolMb;
        var incrementalPhysicalNeed = currentMem > 0 ? extraMb : targetPoolMb;
        var cpu = wm?.CpuCorePercent ?? 0;
        var decision = Decide(cpu, projected, settings, system, incrementalPhysicalNeed, out var capacityReason);
        var canApply = hashOk && decision != "BLOCKIERT";
        var reason = $"WM-Sessionstride 0x{WmClientSessionStride:X} ({WmClientSessionStride:N0} B): +{extraMb:F1} MB Rohpool gegenüber {currentLimit:N0}. {capacityReason}";
        if (!hashOk)
        {
            decision = "BLOCKIERT";
            canApply = false;
            reason = "WorldManager.exe entspricht nicht dem verifizierten NA2016-Baseline-Hash. Runtime-Adressen werden deshalb nicht benutzt.";
        }
        else if (runtime.Success)
        {
            reason += $" Runtime aktuell {runtime.NumSessions:N0}/{runtime.MaxSessions:N0}, g_UserLimit={runtime.UserLimit:N0}.";
        }

        return new AdaptiveHookAssessment
        {
            Scope = "WorldManager", Resource = "Client Sessions", HookType = "CONFIG + LIVE WM",
            CurrentLimit = currentLimit, TargetLimit = target, Decision = decision, Reason = reason,
            CpuCorePercent = cpu, CurrentPrivateMemoryMb = currentMem, ProjectedPrivateMemoryMb = projected,
            ExtraMemoryMb = extraMb, CanApply = canApply, RestartRequired = target != currentLimit
        };
    }

    private AdaptiveHookAssessment AssessSimpleConfig(
        string scope, string resource, string hookType, int current, int target,
        double cpu, double privateMem, double extraMb, AppSettings settings, SystemResourceSnapshot system, string detail, bool hashGate)
    {
        target = Math.Max(1, target);
        var projected = privateMem > 0 ? privateMem + extraMb : extraMb;
        var decision = Decide(cpu, projected, settings, system, extraMb, out var capacityReason);
        if (!hashGate) decision = "BLOCKIERT";
        return new AdaptiveHookAssessment
        {
            Scope = scope, Resource = resource, HookType = hookType,
            CurrentLimit = current, TargetLimit = target,
            Decision = decision,
            Reason = hashGate ? detail + " " + capacityReason : detail,
            CpuCorePercent = cpu, CurrentPrivateMemoryMb = privateMem, ProjectedPrivateMemoryMb = projected,
            ExtraMemoryMb = extraMb, CanApply = hashGate && decision != "BLOCKIERT", RestartRequired = target != current
        };
    }

    private AdaptiveHookAssessment AssessZoneBinaryPool(
        string resource, int current, int target, int stride, double cpu, double privateMem,
        AppSettings settings, SystemResourceSnapshot system, bool hashOk, string dependencyReason)
    {
        target = Math.Max(1, target);
        var extra = Math.Max(0, target - current) * (double)stride / 1024d / 1024d;
        var targetPool = target * (double)stride / 1024d / 1024d;
        var projected = privateMem > 0 ? privateMem + extra : targetPool;
        var capacityDecision = Decide(cpu, projected, settings, system, privateMem > 0 ? extra : targetPool, out var capacityReason);
        var experimentalText = settings.AllowExperimentalZoneBinaryHooks
            ? "Experimentelle Binary-Hooks wurden angefordert, bleiben für diesen Pool aber bis zur vollständigen Handle-Rebase-Verifikation gesperrt."
            : "Experimentelle Binary-Hooks sind deaktiviert.";
        var decision = "BLOCKIERT";
        var reason = !hashOk
            ? "Mindestens eine Zone.exe weicht vom verifizierten NA2016-Baseline-Hash ab."
            : $"{dependencyReason} {experimentalText} Rechnerbewertung wäre: {capacityDecision}. {capacityReason}";
        return new AdaptiveHookAssessment
        {
            Scope = "Zone", Resource = resource, HookType = "BINARY-HOOK (GUARDED)",
            CurrentLimit = current, TargetLimit = target, Decision = decision, Reason = reason,
            CpuCorePercent = cpu, CurrentPrivateMemoryMb = privateMem, ProjectedPrivateMemoryMb = projected,
            ExtraMemoryMb = extra, CanApply = false, RestartRequired = true
        };
    }

    private static string Decide(double cpuCore, double projectedPrivateMb, AppSettings settings, SystemResourceSnapshot system, double extraProcessMb, out string reason)
    {
        var memBudget = Math.Max(1024, settings.ZonePrivateMemoryBudgetMb);
        var memPct = projectedPrivateMb > 0 ? projectedPrivateMb / memBudget * 100.0 : 0;
        if (system.CpuPercent >= 95)
        {
            reason = $"Gesamtsystem-CPU liegt bei {system.CpuPercent:F0}%. Zusätzliche Kapazität würde nur Queueing/Latenz erhöhen.";
            return "BLOCKIERT";
        }
        if (system.TotalMemoryMb > 0 && system.AvailableMemoryMb < Math.Max(1024, extraProcessMb * 2.0))
        {
            reason = $"Nur {system.AvailableMemoryMb:F0} MB physischer RAM frei; für +{extraProcessMb:F0} MB Prozesspool fehlt Sicherheitsreserve.";
            return "BLOCKIERT";
        }
        if (cpuCore >= settings.HookCpuBlockPercent)
        {
            reason = $"CPU liegt bei {cpuCore:F0}% eines Kerns (Blockschwelle {settings.HookCpuBlockPercent}%). Horizontal skalieren statt Limits erhöhen.";
            return "BLOCKIERT";
        }
        if (memPct >= settings.HookMemoryBlockPercent)
        {
            reason = $"Projizierter Privat-RAM liegt bei {memPct:F0}% des konservativen {memBudget:N0}-MB-Budgets.";
            return "BLOCKIERT";
        }
        if (cpuCore >= settings.HookCpuWarnPercent || memPct >= settings.HookMemoryWarnPercent || system.CpuPercent >= 80 || system.MemoryUsedPercent >= 85)
        {
            reason = $"Reserve ist nur mittel (Prozess {cpuCore:F0}% Core, projizierter Prozess-RAM {memPct:F0}% Budget; {system.Summary}). Nur stufenweise erhöhen und Lasttest durchführen.";
            return "WARNUNG";
        }
        reason = $"Aus den aktuell messbaren CPU-/RAM-Werten besteht Reserve (Prozess {cpuCore:F0}% Core, projizierter RAM {memPct:F0}% Budget; {system.Summary}).";
        return "EMPFOHLEN";
    }

    private static bool IsWorldManagerClientEntry(ServerInfoEntry e) =>
        e.Name.Contains("_WM", StringComparison.OrdinalIgnoreCase) && e.ConnectionKind == 20;
    private static bool IsWorldManagerZoneEntry(ServerInfoEntry e) =>
        e.Name.Contains("_WM", StringComparison.OrdinalIgnoreCase) && e.ConnectionKind == 6;

    private static bool IsWorldManagerClientLine(string name, int kind) => name.Contains("_WM", StringComparison.OrdinalIgnoreCase) && kind == 20;
    private static bool IsWorldManagerZoneLine(string name, int kind) => name.Contains("_WM", StringComparison.OrdinalIgnoreCase) && kind == 6;
    private static bool IsZoneClientLine(string name, int kind) => name.Contains("_Z", StringComparison.OrdinalIgnoreCase) && !name.Contains("_WM", StringComparison.OrdinalIgnoreCase) && kind == 20;

    private static int RewriteMaxAccept(List<string> lines, Func<string, int, bool> predicate, int newValue)
    {
        var count = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var m = ServerInfoLineRx.Match(lines[i]);
            if (!m.Success || !int.TryParse(m.Groups["kind"].Value, out var kind)) continue;
            var name = m.Groups["name"].Value;
            if (!predicate(name, kind)) continue;
            lines[i] = m.Groups["prefix"].Value + newValue + m.Groups["suffix"].Value;
            count++;
        }
        return count;
    }

    private static bool HashEquals(string path, string expected)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static WmRuntimeState TryReadWorldManagerRuntime(FiestaServiceEntry wm)
    {
        if (!wm.ProcessId.HasValue || wm.ProcessId.Value <= 0)
            return new WmRuntimeState(false, 0, 0, 0, "WM-PID nicht verfügbar");
        if (!File.Exists(wm.ExecutablePath) || !HashEquals(wm.ExecutablePath, BaselineWorldManagerSha256))
            return new WmRuntimeState(false, 0, 0, 0, "WM-Baseline-Hash passt nicht");
        try
        {
            using var process = Process.GetProcessById(wm.ProcessId.Value);
            var baseAddress = process.MainModule?.BaseAddress ?? IntPtr.Zero;
            if (baseAddress == IntPtr.Zero) return new WmRuntimeState(false, 0, 0, 0, "WM-Modulbasis nicht lesbar");
            var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, wm.ProcessId.Value);
            if (handle == IntPtr.Zero) return new WmRuntimeState(false, 0, 0, 0, "OpenProcess fehlgeschlagen");
            try
            {
                if (!TryReadInt32(handle, baseAddress, WmUserLimitRva, out var userLimit)
                    || !TryReadInt32(handle, baseAddress, WmMaxSessionsRva, out var maxSessions)
                    || !TryReadInt32(handle, baseAddress, WmNumSessionsRva, out var numSessions))
                    return new WmRuntimeState(false, 0, 0, 0, "WM-Runtimezähler konnten nicht gelesen werden");
                if (maxSessions < 1 || maxSessions > 100000 || numSessions < 0 || numSessions > maxSessions)
                    return new WmRuntimeState(false, 0, 0, 0, "WM-Runtimewerte wirken unplausibel; Hook abgebrochen");
                return new WmRuntimeState(true, userLimit, maxSessions, numSessions, "OK");
            }
            finally { CloseHandle(handle); }
        }
        catch (Exception ex) { return new WmRuntimeState(false, 0, 0, 0, ex.Message); }
    }

    private static (bool Success, string Detail) TrySetWorldManagerUserLimit(FiestaServiceEntry wm, int target)
    {
        var state = TryReadWorldManagerRuntime(wm);
        if (!state.Success) return (false, state.Detail);
        if (target < state.NumSessions) return (false, $"Ziel {target:N0} liegt unter den aktuell {state.NumSessions:N0} aktiven Sessions.");
        if (target > state.MaxSessions) return (false, $"Ziel {target:N0} liegt über m_MaxSessions={state.MaxSessions:N0}; erst ServerInfo ändern und WM neu starten.");
        try
        {
            using var process = Process.GetProcessById(wm.ProcessId!.Value);
            var baseAddress = process.MainModule?.BaseAddress ?? IntPtr.Zero;
            if (baseAddress == IntPtr.Zero) return (false, "WM-Modulbasis nicht lesbar");
            var handle = OpenProcess(ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation, false, wm.ProcessId.Value);
            if (handle == IntPtr.Zero) return (false, "OpenProcess für Live-Hook fehlgeschlagen (als Administrator starten).");
            try
            {
                var bytes = BitConverter.GetBytes(target);
                var address = IntPtr.Add(baseAddress, checked((int)WmUserLimitRva));
                if (!WriteProcessMemory(handle, address, bytes, bytes.Length, out var written) || written.ToInt64() != bytes.Length)
                    return (false, "WriteProcessMemory für g_UserLimit fehlgeschlagen.");
                if (!TryReadInt32(handle, baseAddress, WmUserLimitRva, out var verify) || verify != target)
                    return (false, "Live-Hook konnte nicht verifiziert werden.");
                return (true, $"WM g_UserLimit wurde live auf {target:N0} gesetzt (m_MaxSessions={state.MaxSessions:N0}).");
            }
            finally { CloseHandle(handle); }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static bool TryReadInt32(IntPtr process, IntPtr baseAddress, long rva, out int value)
    {
        value = 0;
        var buffer = new byte[4];
        var address = IntPtr.Add(baseAddress, checked((int)rva));
        if (!ReadProcessMemory(process, address, buffer, buffer.Length, out var read) || read.ToInt64() != 4) return false;
        value = BitConverter.ToInt32(buffer, 0);
        return true;
    }

    private sealed record WmRuntimeState(bool Success, int UserLimit, int MaxSessions, int NumSessions, string Detail);

    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
