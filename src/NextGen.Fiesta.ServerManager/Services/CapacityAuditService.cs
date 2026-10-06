using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class CapacityAuditResult
{
    public IReadOnlyList<CapacityLimitEntry> Limits { get; init; } = Array.Empty<CapacityLimitEntry>();
    public IReadOnlyList<MapCapacityEntry> Maps { get; init; } = Array.Empty<MapCapacityEntry>();
    public string Summary { get; init; } = string.Empty;
}

public sealed class CapacityAuditService
{
    public CapacityAuditResult Analyze(string serverRoot)
    {
        var limits = BuildVerifiedLimits();
        var fieldRows = ReadFieldList(serverRoot);
        var maps = ReadMaps(serverRoot, fieldRows);

        var validMaps = maps.Where(x => x.HasShbd && x.PayloadValid && x.CollisionWidthCells.HasValue && x.CollisionHeightCells.HasValue).ToList();
        var largestPayload = validMaps.OrderByDescending(x => x.PayloadBytes ?? 0).FirstOrDefault();
        var largestFieldArea = maps.Where(x => x.FieldX.HasValue && x.FieldY.HasValue)
            .OrderByDescending(x => (long)x.FieldX!.Value * x.FieldY!.Value).FirstOrDefault();

        var summaryParts = new List<string>
        {
            $"{limits.Count} verifizierte Limits",
            $"{maps.Count(x => x.HasShbd)} SHBD-Dateien analysiert"
        };

        if (largestPayload is not null)
        {
            var tied = validMaps.Where(x => x.PayloadBytes == largestPayload.PayloadBytes).Select(x => x.MapId).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
            summaryParts.Add($"größter SHBD: {string.Join(" / ", tied)} – {largestPayload.CollisionGridText} Kollisionszellen, Server-Blockfläche {largestPayload.ServerBlockWorldSizeText}, {largestPayload.FileSizeText}");
        }
        if (largestFieldArea is not null)
            summaryParts.Add($"größter Field.txt-Eintrag: {largestFieldArea.MapId} – {largestFieldArea.FieldSizeText}, logisch ca. {largestFieldArea.FieldWorldSizeText} Koordinateneinheiten");

        return new CapacityAuditResult
        {
            Limits = limits,
            Maps = maps,
            Summary = string.Join(" · ", summaryParts)
        };
    }

    private static List<CapacityLimitEntry> BuildVerifiedLimits() => new()
    {
        L("Zone", "Objektpool", "ShinePlayer", 1500, 0x2C058, "ShineObjectManager::som_Initialize: count 0x5DC, stride 0x2C058", "Harter Objektpool. Entspricht auch dem Stock-nMaxAccept der Zone, ist aber eine separate Grenze."),
        L("Zone", "Objektpool", "ShineMob", 8000, 0x2568, "som_Initialize: count 0x1F40, stride 0x2568", "Globaler ShineMob-Pool pro Zone-Prozess."),
        L("Zone", "Objektpool", "ShineNPC", 256, 0x256C, "som_Initialize: count 0x100, stride 0x256C", "Globaler ShineNPC-Pool pro Zone-Prozess."),
        L("Zone", "Objektpool", "ShineBandit", 2048, 0x266C, "som_Initialize: count 0x800, stride 0x266C", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShinePet", 1500, 0x25D4, "som_Initialize: count 0x5DC, stride 0x25D4", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShineMover", 1000, 0x202C, "som_Initialize: count 0x3E8, stride 0x202C", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShineServant", 500, 0x2598, "som_Initialize: count 0x1F4, stride 0x2598", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShineMiniHouse", 1000, 0xD100, "som_Initialize: count 0x3E8, stride 0xD100", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShineMagicField", 250, 0x1C8, "som_Initialize: count 0xFA, stride 0x1C8", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShineDoor", 1000, 0x1F14, "som_Initialize: count 0x3E8, stride 0x1F14", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShineEffectObject", 1000, 0x1D3, "som_Initialize: count 0x3E8, stride 0x1D3", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShineDropItem", 3000, 0x28B, "som_Initialize: count 0xBB8, stride 0x28B", "Harter Objektpool."),
        L("Zone", "Objektpool", "ShineAxialFlag", 3584, 0x188, "som_Initialize: count 0xE00, stride 0x188", "Harter Objektpool."),
        L("Zone", "Map", "MapCluster Registry", 512, 0, "cmp ..., 0x200 vor 'Too many mapcluster'", "Registrierte MapCluster-Einträge; nicht mit Kartenbreite/-höhe verwechseln."),
        L("Zone", "Map", "MapBlockInformation", 256, 0, "cmp ..., 0x100 vor 'Too many block info'", "Kapazität des BlockInfo-Containers."),
        L("Zone", "Map", "BlockDistribute", 64, 0, "cmp ..., 0x40 vor 'Too many BlockDistribute'", "BlockDistribute-Manager-Einträge; keine direkte Kartenabmessung."),
        L("Zone", "Kingdom Quest", "KQ Entrance", 100, 0, "Vergleich mit 0x64 vor 'Too many entrance'", "Entrance-/Eingangspuffer."),
        L("Zone", "Quest", "MAXQUEST-Pfad", 3000, 0, "Vergleich mit 0x0BB8 vor 'Too Many Quest - MAXQUEST'", "Kontextbezogene Quest-Daten-Grenze, nicht 3000 aktive Quests pro Spieler."),
        L("WorldManager", "Kingdom Quest", "KQ Buffer", 300, 0, "cmp ..., 0x12C vor MAX_KINGDOM_QUEST BUFFER OVERFLOW", "WM-KQ-Verwaltungspuffer."),
        L("WorldManager", "Guild", "Guild Count", 16384, 0, "cmp ..., 0x4000 vor 'Max Guild Count Over'", "Globaler WM-Gildenpuffer."),
        L("WorldManager", "Protokoll", "Friend List packet path", 100, 0, "cmp ..., 0x64 vor 'OVER MAX FRIEND'", "Paket-/Listen-Grenze des geprüften Pfads."),
        L("WorldManager", "Protokoll", "Chat Restrict packet path", 100, 0, "cmp ..., 0x64 vor 'OVER MAX CHAT RESTRICT'", "Paket-/Listen-Grenze des geprüften Pfads."),
        L("Zone", "Socket", "Client nMaxAccept", 1500, 0, "ServerInfo: nBackLog=100, nMaxAccept=1500", "Maximal akzeptierte Client-Sessions am Listener; nicht alleinige Stabilitätsgarantie."),
        L("WorldManager", "Socket", "Client nMaxAccept", 1500, 0, "ServerInfo: WM Client nMaxAccept=1500", "Client-/WM-Sessions."),
        L("WorldManager", "Socket", "Zone nMaxAccept", 100, 0, "ServerInfo: WM Zone listener nMaxAccept=100", "Socket-Sessions, nicht Anzahl Zone-IDs."),
    };

    private static CapacityLimitEntry L(string scope, string category, string name, int limit, int objectSize, string evidence, string notes) => new()
    {
        Scope = scope,
        Category = category,
        Name = name,
        HardLimit = limit,
        ObjectSizeBytes = objectSize,
        Evidence = evidence,
        Notes = notes
    };

    private sealed record FieldRow(string MapId, string MapName, int X, int Y, int? Zone);

    private static List<FieldRow> ReadFieldList(string serverRoot)
    {
        var path = Path.Combine(serverRoot, "9Data", "Shine", "World", "Field.txt");
        if (!File.Exists(path)) return new List<FieldRow>();

        var rows = new List<FieldRow>();
        var inFieldList = false;
        Dictionary<string, int>? columns = null;
        foreach (var raw in File.ReadLines(path, System.Text.Encoding.Latin1))
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.StartsWith("#Table\t", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split('\t');
                var table = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                inFieldList = string.Equals(table, "FieldList", StringComparison.OrdinalIgnoreCase);
                columns = null;
                continue;
            }
            if (!inFieldList) continue;

            if (line.StartsWith("#ColumnName\t", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split('\t');
                columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < parts.Length; i++)
                {
                    var name = parts[i].Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !columns.ContainsKey(name)) columns[name] = i;
                }
                continue;
            }

            if (columns is null || !line.StartsWith("#Record\t", StringComparison.OrdinalIgnoreCase)) continue;
            var t = line.Split('\t'); // bewusst Leerfelder erhalten: Field.txt enthält reservierte/leere Spalten.
            string Get(string name) => columns.TryGetValue(name, out var i) && i < t.Length ? t[i].Trim() : string.Empty;

            var mapId = Get("MapIDClient");
            var mapName = Get("MapName");
            if (string.IsNullOrWhiteSpace(mapId) || !int.TryParse(Get("xsize"), out var x) || !int.TryParse(Get("ysize"), out var y)) continue;
            int? zone = int.TryParse(Get("Fiesta"), out var z) ? z : null;
            rows.Add(new FieldRow(mapId, mapName.Replace('#', ' '), x, y, zone));
        }
        return rows;
    }

    private static List<MapCapacityEntry> ReadMaps(string serverRoot, List<FieldRow> fieldRows)
    {
        var blockInfo = Path.Combine(serverRoot, "9Data", "Shine", "BlockInfo");
        var byMap = fieldRows.GroupBy(x => x.MapId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var ids = new HashSet<string>(byMap.Keys, StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(blockInfo))
            foreach (var f in Directory.EnumerateFiles(blockInfo, "*.shbd", SearchOption.TopDirectoryOnly)) ids.Add(Path.GetFileNameWithoutExtension(f));

        var result = new List<MapCapacityEntry>();
        foreach (var mapId in ids)
        {
            byMap.TryGetValue(mapId, out var fields);
            fields ??= new List<FieldRow>();
            var f = fields.FirstOrDefault();
            var entry = new MapCapacityEntry
            {
                MapId = mapId,
                MapName = f?.MapName ?? mapId,
                FieldX = fields.Count > 0 ? fields.Max(x => x.X) : null,
                FieldY = fields.Count > 0 ? fields.Max(x => x.Y) : null,
                Zones = string.Join(", ", fields.Where(x => x.Zone.HasValue).Select(x => x.Zone!.Value).Distinct().OrderBy(x => x).Select(x => x == 99 ? "99" : $"Z{x:00}"))
            };

            var shbd = Path.Combine(blockInfo, mapId + ".shbd");
            entry.ShbdPath = shbd;
            if (!File.Exists(shbd))
            {
                entry.Status = "SHBD fehlt";
                result.Add(entry);
                continue;
            }

            try
            {
                entry.HasShbd = true;
                entry.FileBytes = new FileInfo(shbd).Length;
                using var fs = new FileStream(shbd, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var br = new BinaryReader(fs);
                if (fs.Length < 8)
                {
                    entry.Status = "Header zu kurz";
                    result.Add(entry);
                    continue;
                }
                var x = br.ReadInt32();
                var y = br.ReadInt32();
                entry.ShbdXBytes = x;
                entry.ShbdYRows = y;
                if (x <= 0 || y <= 0)
                {
                    entry.Status = "Ungültige Dimension";
                    result.Add(entry);
                    continue;
                }

                var payload = (long)x * y;
                entry.PayloadBytes = payload;
                entry.PayloadValid = payload >= 0 && 8L + payload == entry.FileBytes;
                entry.Status = entry.PayloadValid ? "OK" : $"Größe abweichend (erwartet {8L + payload:N0} B)";
            }
            catch (Exception ex)
            {
                entry.Status = "Lesefehler: " + ex.Message;
            }
            result.Add(entry);
        }

        return result
            .OrderByDescending(x => x.PayloadBytes ?? -1)
            .ThenBy(x => x.MapId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
