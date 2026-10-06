using System.Text;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class PerformanceTuningAuditService
{
    private const double Conservative32BitPrivateBudgetMb = 3072.0;
    private readonly Dictionary<string, StaticInspection> _staticCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record StaticInspection(
        long ExeLength,
        DateTime ExeWriteUtc,
        long PdbLength,
        DateTime PdbWriteUtc,
        bool IsPe32,
        bool LargeAddressAware,
        bool HasIocpWorkers,
        bool HasMainThreadSymbol);

    public PerformanceTuningAuditResult Analyze(string serverRoot, IReadOnlyList<FiestaServiceEntry> services)
    {
        var candidates = BuildCandidates();
        var processes = new List<ProcessScalingSnapshot>();

        foreach (var service in services.Where(x => x.Kind != FiestaServiceKind.Unknown).OrderBy(x => x.Kind).ThenBy(x => x.ZoneNumber))
        {
            var mainSymbol = service.Kind switch
            {
                FiestaServiceKind.Zone => "ZoneServer::zs_mainthreadfunction",
                FiestaServiceKind.WorldManager => "WorldManagerServer::MainThread",
                _ => string.Empty
            };
            var si = InspectStatic(service.ExecutablePath, service.PdbPath, mainSymbol);
            var limit = service.Kind is FiestaServiceKind.Zone or FiestaServiceKind.WorldManager ? 1500 : 0;
            processes.Add(BuildProcessSnapshot(service.DisplayName, service, limit, si));
        }

        var runningZones = services.Count(x => x.Kind == FiestaServiceKind.Zone && x.State == ServiceRuntimeState.Running);
        var saturatedZones = services.Count(x => x.Kind == FiestaServiceKind.Zone && x.State == ServiceRuntimeState.Running && x.CpuCorePercent >= 85);
        var laa = processes.Where(x => x.IsPe32).All(x => x.LargeAddressAware);
        var summary = $"{processes.Count} Kernkomponenten geprüft · {candidates.Count} Tuning-Kandidaten · " +
                      $"{runningZones} laufende Zonen, davon {saturatedZones} mit ≥85% eines CPU-Kerns · " +
                      (laa ? "PE32/LAA bestätigt – Speicher kann vertikal erweitert werden, bleibt aber 32-Bit-begrenzt." : "PE/LAA nicht für alle Kernkomponenten bestätigt.");

        return new PerformanceTuningAuditResult
        {
            Candidates = candidates,
            Processes = processes,
            Summary = summary
        };
    }

    private static IReadOnlyList<PerformanceTuningEntry> BuildCandidates()
    {
        static double ExtraMb(int current, int target, int stride) => Math.Max(0, target - current) * (double)stride / 1024d / 1024d;
        return new List<PerformanceTuningEntry>
        {
            new()
            {
                Scope = "WorldManager", Resource = "Client Sessions", CurrentLimit = 1500, SuggestedTarget = 3000,
                Method = "Adaptive Config-Hook + hashgebundener Live-g_UserLimit-Hook",
                ExtraRawMemoryMb = ExtraMb(1500, 3000, 0x1F7B8),
                Risk = "MITTEL", Impact = "Erhöht globale Spieler-Sessions; bringt nur etwas, wenn WM-CPU/RAM Reserve hat.",
                Evidence = "InitSessions(maxSessions) alloziert dynamisch count×0x1F7B8; m_MaxSessions/g_UserLimit sind im exakten 2016-WM verifiziert."
            },
            new()
            {
                Scope = "WorldManager", Resource = "Zone/S2S Sessions", CurrentLimit = 100, SuggestedTarget = 150,
                Method = "ServerInfo nMaxAccept + SessionManager-Verifikation",
                Risk = "NIEDRIG–MITTEL", Impact = "Mehr interne Zone/WM-Sockets; kein direkter Spieler-Throughput-Gewinn.",
                Evidence = "Stock WM-Zonelistener nMaxAccept=100; Sessionmanager ist dynamisch aufgebaut."
            },
            new()
            {
                Scope = "Zone", Resource = "ShinePlayer", CurrentLimit = 1500, SuggestedTarget = 2000,
                Method = "Binary/Hook + Zone nMaxAccept; niemals nur ServerInfo erhöhen",
                ExtraRawMemoryMb = ExtraMb(1500, 2000, 0x2C058), Risk = "HOCH",
                Impact = "Mehr Spieler pro Zone. Hauptthread/Visibility/Packet-Fanout können vor dem Speicherlimit sättigen.",
                Evidence = "som_Initialize count=1500, stride=0x2C058; separater harter Objektpool."
            },
            new()
            {
                Scope = "Zone", Resource = "ShineMob", CurrentLimit = 8000, SuggestedTarget = 12000,
                Method = "Binary/Hook + gezielter Lasttest",
                ExtraRawMemoryMb = ExtraMb(8000, 12000, 0x2568), Risk = "MITTEL–HOCH",
                Impact = "Mehr Mobs pro Zone; AI/Tickkosten steigen deutlich und können CPU vor Poolgrenze sättigen.",
                Evidence = "som_Initialize count=8000, stride=0x2568."
            },
            new()
            {
                Scope = "Zone", Resource = "ShineNPC", CurrentLimit = 256, SuggestedTarget = 512,
                Method = "Binary/Hook + Funktionstest NPC-Menüs/Handles",
                ExtraRawMemoryMb = ExtraMb(256, 512, 0x256C), Risk = "MITTEL",
                Impact = "Mehr statische NPCs pro Zone bei relativ geringem zusätzlichem Rohspeicher.",
                Evidence = "som_Initialize count=256, stride=0x256C."
            },
            new()
            {
                Scope = "Zone", Resource = "MapBlockInformation", CurrentLimit = 256, SuggestedTarget = 384,
                Method = "Binary/Hook; Container- und abhängige Indexprüfungen nötig",
                Risk = "HOCH", Impact = "Mehr Maps pro Zone, erhöht aber Grundlast und verschärft 32-Bit-/Mainthread-Druck.",
                Evidence = "cmp ..., 0x100 vor 'Too many block info'."
            },
            new()
            {
                Scope = "Zone", Resource = "MapCluster Registry", CurrentLimit = 512, SuggestedTarget = 768,
                Method = "Binary/Hook; abhängige Arrays/Indexbreiten prüfen",
                Risk = "HOCH", Impact = "Mehr MapCluster; nicht automatisch mehr Spielerleistung.",
                Evidence = "cmp ..., 0x200 vor 'Too many mapcluster'."
            }
        };
    }

    private ProcessScalingSnapshot BuildProcessSnapshot(string component, FiestaServiceEntry service, int clientLimit, StaticInspection si)
    {
        var cpu = Math.Max(0, service.CpuCorePercent);
        var mem = Math.Max(0, service.PrivateMemoryMb);
        var privateBudgetMb = si.IsPe32 ? (si.LargeAddressAware ? Conservative32BitPrivateBudgetMb : 1792.0) : Conservative32BitPrivateBudgetMb;
        var sessions = Math.Max(0, service.EstablishedClientConnections);
        int? cpuCeiling = null;
        if (sessions >= 50 && cpu >= 5)
        {
            // Deliberately conservative and explicitly only a linear first-order estimate.
            cpuCeiling = (int)Math.Max(sessions, Math.Round(sessions * 80.0 / cpu));
        }

        string headroom;
        string recommendation;
        if (service.State != ServiceRuntimeState.Running)
        {
            headroom = "nicht messbar";
            recommendation = "Komponente starten und mehrere Minuten unter realer Last messen.";
        }
        else if (cpu >= 90)
        {
            headroom = "GERING – CPU/Mainthread";
            recommendation = "Pools nicht blind erhöhen: CPU ist bereits praktisch ein Kern voll. Mehr/fairer verteilte Zonen bringen mehr als höhere Limits.";
        }
        else if (mem >= privateBudgetMb * 0.9)
        {
            headroom = "GERING – 32-Bit-Speicher";
            recommendation = "Privat-RAM liegt nahe am konservativen 3-GB-Betriebsbudget. Erst Speicher/Fragmentierung reduzieren oder Architektur ändern.";
        }
        else if (cpu >= 70 || mem >= privateBudgetMb * 0.7)
        {
            headroom = "MITTEL";
            recommendation = "Moderates vertikales Tuning möglich; nach jeder Änderung Lasttest und P95/P99-Latenz beobachten.";
        }
        else
        {
            headroom = "GUT";
            recommendation = si.HasMainThreadSymbol
                ? "Vertikale Reserve vorhanden. Zuerst Session-/Poolgrenzen kontrolliert erhöhen; Mainthread bleibt später die natürliche CPU-Grenze."
                : "Vertikale Reserve vorhanden; Threadmodell vor aggressiver Erhöhung weiter verifizieren.";
        }

        return new ProcessScalingSnapshot
        {
            Component = component,
            BinaryPath = service.ExecutablePath,
            IsPe32 = si.IsPe32,
            LargeAddressAware = si.LargeAddressAware,
            HasIocpWorkers = si.HasIocpWorkers,
            HasMainThreadSymbol = si.HasMainThreadSymbol,
            CpuCorePercent = cpu,
            PrivateMemoryMb = service.PrivateMemoryMb,
            VirtualMemoryMb = service.VirtualMemoryMb,
            ThreadCount = service.ThreadCount,
            HandleCount = service.HandleCount,
            ClientSessions = sessions,
            ClientLimit = clientLimit,
            EstimatedCpuCeiling = cpuCeiling,
            VerticalHeadroom = headroom,
            Recommendation = recommendation
        };
    }

    private StaticInspection InspectStatic(string exePath, string? pdbPath, string mainThreadSymbol)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return new StaticInspection(0, DateTime.MinValue, 0, DateTime.MinValue, false, false, false, false);

        var exeInfo = new FileInfo(exePath);
        var actualPdb = !string.IsNullOrWhiteSpace(pdbPath) && File.Exists(pdbPath)
            ? pdbPath
            : Path.ChangeExtension(exePath, ".pdb");
        var pdbInfo = File.Exists(actualPdb) ? new FileInfo(actualPdb) : null;
        var key = exePath + "|" + actualPdb;

        if (_staticCache.TryGetValue(key, out var cached)
            && cached.ExeLength == exeInfo.Length && cached.ExeWriteUtc == exeInfo.LastWriteTimeUtc
            && cached.PdbLength == (pdbInfo?.Length ?? 0) && cached.PdbWriteUtc == (pdbInfo?.LastWriteTimeUtc ?? DateTime.MinValue))
            return cached;

        var (isPe32, laa) = ReadPeFlags(exePath);
        var hasIocp = File.Exists(actualPdb) && (BinaryContainsAscii(actualPdb, "CIOCP::WorkThread") || BinaryContainsAscii(actualPdb, "CIOCP::Start"));
        var hasMain = File.Exists(actualPdb) && (!string.IsNullOrWhiteSpace(mainThreadSymbol)
            ? BinaryContainsAscii(actualPdb, mainThreadSymbol)
            : (BinaryContainsAscii(actualPdb, "MainThread") || BinaryContainsAscii(actualPdb, "mainthreadfunction")));
        var inspection = new StaticInspection(
            exeInfo.Length, exeInfo.LastWriteTimeUtc,
            pdbInfo?.Length ?? 0, pdbInfo?.LastWriteTimeUtc ?? DateTime.MinValue,
            isPe32, laa, hasIocp, hasMain);
        _staticCache[key] = inspection;
        return inspection;
    }

    private static (bool IsPe32, bool LargeAddressAware) ReadPeFlags(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return (false, false); // MZ
            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 26) return (false, false);
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return (false, false); // PE\0\0
            _ = br.ReadUInt16(); // machine
            _ = br.ReadUInt16(); // sections
            _ = br.ReadUInt32(); _ = br.ReadUInt32(); _ = br.ReadUInt32();
            _ = br.ReadUInt16(); // optional header size
            var characteristics = br.ReadUInt16();
            var magic = br.ReadUInt16();
            return (magic == 0x10B, (characteristics & 0x20) != 0);
        }
        catch { return (false, false); }
    }

    private static bool BinaryContainsAscii(string path, string value)
    {
        try
        {
            var needle = Encoding.ASCII.GetBytes(value);
            if (needle.Length == 0) return true;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024 + needle.Length];
            var carry = 0;
            while (true)
            {
                var read = fs.Read(buffer, carry, 64 * 1024);
                if (read <= 0) return false;
                var total = carry + read;
                if (IndexOf(buffer, total, needle) >= 0) return true;
                carry = Math.Min(needle.Length - 1, total);
                Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
            }
        }
        catch { return false; }
    }

    private static int IndexOf(byte[] haystack, int haystackLength, byte[] needle)
    {
        for (var i = 0; i <= haystackLength - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                match = false;
                break;
            }
            if (match) return i;
        }
        return -1;
    }
}
