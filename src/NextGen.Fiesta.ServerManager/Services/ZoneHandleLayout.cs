namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Pure, read-only model of the verified NA2016 Zone 16-bit Shine object handle layout.
/// It never touches Zone.exe. The planner exists so binary patching can only be considered
/// after a complete, non-overlapping target layout has been calculated and validated.
/// </summary>
public static class ZoneHandleLayout
{
    public const int HandleSpaceSize = 0x10000;
    public const int ReservedGapSize = 500;

    public const int StockMob = 8000;
    public const int StockPlayer = 1500;
    public const int StockNpc = 256;

    private static readonly SegmentDefinition[] Definitions =
    [
        new("ShineMob", 5, StockMob),
        new("ShinePlayer", 2, StockPlayer),
        new("ShineEffectObject", 3, 1000),
        new("ShineDropItem", 1, 3000),
        new("ShineAxialFlag", 0, 3584),
        new("ShineNPC", 4, StockNpc),
        new("ShineBandit", 8, 2048),
        new("ShineMiniHouse", 9, 1000),
        new("ShineMagicField", 6, 250),
        new("ShineDoor", 7, 1000),
        new("ShineServant", 10, 500),
        new("ShineMover", 11, 1000),
        new("RESERVED_INVALID", null, ReservedGapSize, true),
        new("ShinePet", 12, 1500)
    ];

    private static readonly IReadOnlyDictionary<string, int> VerifiedStockStarts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ShineMob"] = 0x0000,
            ["ShinePlayer"] = 0x1F40,
            ["ShineEffectObject"] = 0x251C,
            ["ShineDropItem"] = 0x2904,
            ["ShineAxialFlag"] = 0x34BC,
            ["ShineNPC"] = 0x42BC,
            ["ShineBandit"] = 0x43BC,
            ["ShineMiniHouse"] = 0x4BBC,
            ["ShineMagicField"] = 0x4FA4,
            ["ShineDoor"] = 0x509E,
            ["ShineServant"] = 0x5486,
            ["ShineMover"] = 0x567A,
            ["RESERVED_INVALID"] = 0x5A62,
            ["ShinePet"] = 0x5C56
        };

    public static ZoneHandleRebasePlan StockPlan { get; } = Build(StockPlayer, StockMob, StockNpc);

    public static ZoneHandleRebasePlan Plan(int playerCapacity, int mobCapacity, int npcCapacity)
        => Build(playerCapacity, mobCapacity, npcCapacity);

    private static ZoneHandleRebasePlan Build(int playerCapacity, int mobCapacity, int npcCapacity)
    {
        if (playerCapacity < 1 || mobCapacity < 1 || npcCapacity < 1)
            return Invalid("Player-, Mob- und NPC-Kapazität müssen größer als 0 sein.");

        if (playerCapacity > ushort.MaxValue || mobCapacity > ushort.MaxValue || npcCapacity > ushort.MaxValue)
            return Invalid("Ein einzelner Pool kann im 16-Bit-Handlemodell nicht größer als 65.535 sein.");

        var ranges = new List<ZoneHandleRange>(Definitions.Length);
        var cursor = 0;

        foreach (var definition in Definitions)
        {
            var capacity = definition.Name switch
            {
                "ShineMob" => mobCapacity,
                "ShinePlayer" => playerCapacity,
                "ShineNPC" => npcCapacity,
                _ => definition.StockCapacity
            };

            if (capacity < 1)
                return Invalid($"Ungültige Kapazität für {definition.Name}: {capacity:N0}.");

            var endExclusive = (long)cursor + capacity;
            if (endExclusive > HandleSpaceSize)
                return Invalid($"Handle-Space überläuft bei {definition.Name}: Ende 0x{endExclusive - 1:X4} liegt außerhalb 0xFFFF.", ranges);

            var stockStart = VerifiedStockStarts[definition.Name];
            ranges.Add(new ZoneHandleRange(
                definition.Name,
                definition.TypeCode,
                stockStart,
                cursor,
                definition.StockCapacity,
                capacity,
                definition.IsReserved));
            cursor = (int)endExclusive;
        }

        for (var i = 1; i < ranges.Count; i++)
        {
            if (ranges[i - 1].End + 1 != ranges[i].Start)
                return Invalid($"Interner Layoutfehler zwischen {ranges[i - 1].Name} und {ranges[i].Name}.", ranges);
        }

        var stockShapeOk = StockStartsMatchVerifiedLayout();
        if (!stockShapeOk)
            return Invalid("Die eingebettete Stock-Matrix stimmt nicht mit den verifizierten NA2016-Handlegrenzen überein.", ranges);

        var changed = ranges.Where(x => x.Changed).ToArray();
        var detail = changed.Length == 0
            ? $"Stock-Layout verifiziert; erster unbenutzter Handle ist 0x{cursor:X4}."
            : $"Dry-Run gültig: {changed.Length} Bereich(e) ändern sich; erster unbenutzter Handle wird 0x{cursor:X4}. Keine Binärdatei wurde verändert.";

        return new ZoneHandleRebasePlan
        {
            IsValid = true,
            Detail = detail,
            Ranges = ranges,
            FirstUnusedHandle = cursor,
            RemainingHandles = HandleSpaceSize - cursor
        };
    }

    private static bool StockStartsMatchVerifiedLayout()
    {
        var cursor = 0;
        foreach (var definition in Definitions)
        {
            if (VerifiedStockStarts[definition.Name] != cursor)
                return false;
            cursor += definition.StockCapacity;
        }
        return cursor == 0x6232;
    }

    private static ZoneHandleRebasePlan Invalid(string detail, IReadOnlyList<ZoneHandleRange>? ranges = null)
        => new()
        {
            IsValid = false,
            Detail = detail,
            Ranges = ranges ?? Array.Empty<ZoneHandleRange>(),
            FirstUnusedHandle = ranges is { Count: > 0 } ? ranges[^1].End + 1 : 0,
            RemainingHandles = 0
        };

    private sealed record SegmentDefinition(string Name, byte? TypeCode, int StockCapacity, bool IsReserved = false);
}

public sealed record ZoneHandleRange(
    string Name,
    byte? TypeCode,
    int StockStart,
    int Start,
    int StockCapacity,
    int Capacity,
    bool IsReserved)
{
    public int End => Start + Capacity - 1;
    public int StockEnd => StockStart + StockCapacity - 1;
    public bool Changed => Start != StockStart || Capacity != StockCapacity;
    public string RangeText => $"0x{Start:X4}-0x{End:X4}";
    public string StockRangeText => $"0x{StockStart:X4}-0x{StockEnd:X4}";
}

public sealed class ZoneHandleRebasePlan
{
    public bool IsValid { get; init; }
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ZoneHandleRange> Ranges { get; init; } = Array.Empty<ZoneHandleRange>();
    public int FirstUnusedHandle { get; init; }
    public int RemainingHandles { get; init; }
}
