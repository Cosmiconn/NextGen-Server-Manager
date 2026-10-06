using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Hash-bound, read-only evidence audit for Player/Mob/NPC pool control-flow.
/// These sites are not patch sites. They prove that representative spawn/allocation
/// paths delegate to ShineObjectManager::som_AllocObject and that selected numeric
/// look-alikes are unrelated to Player/Mob/NPC pool capacity.
/// </summary>
public sealed class ZonePoolControlFlowAudit
{
    public const int KnownEvidenceSiteCount = 13;

    public ZonePoolControlFlowAuditResult Analyze(string zoneExePath)
    {
        if (string.IsNullOrWhiteSpace(zoneExePath) || !File.Exists(zoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.");

        byte[] image;
        try { image = File.ReadAllBytes(zoneExePath); }
        catch (Exception ex) { return Failed("Zone.exe konnte nicht gelesen werden: " + ex.Message); }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (!hash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Zone.exe-Hash weicht vom verifizierten NA2016-Build ab.", hash);

        var definitions = BuildDefinitions();
        var checks = new List<ZonePoolControlFlowEvidenceCheck>(definitions.Count);
        foreach (var site in definitions)
        {
            if (!TryVirtualAddressToFileOffset(image, site.VirtualAddress, out var offset))
            {
                checks.Add(ToCheck(site, -1, Array.Empty<byte>(), false, "VA NICHT ABBILDBAR"));
                continue;
            }

            var available = Math.Max(0, Math.Min(site.Expected.Length, image.Length - offset));
            var actual = image.AsSpan(offset, available).ToArray();
            var matches = available == site.Expected.Length && actual.AsSpan().SequenceEqual(site.Expected);
            checks.Add(ToCheck(site, offset, actual, matches));
        }

        var countOk = definitions.Count == KnownEvidenceSiteCount;
        var verified = countOk && checks.All(x => x.Matches);
        var failed = checks.Count(x => !x.Matches) + (countOk ? 0 : 1);
        return new ZonePoolControlFlowAuditResult
        {
            HashMatches = true,
            EvidenceVerified = verified,
            BinarySha256 = hash,
            Sites = checks,
            Detail = verified
                ? $"Control-Flow-Audit OK: {checks.Count}/{KnownEvidenceSiteCount} hashgebundene Belegstellen bytegenau verifiziert. Das beweist die klassifizierten Pfade, ersetzt aber nicht den vollständigen Patch-Preflight."
                : $"Control-Flow-Audit FEHLGESCHLAGEN: {failed} Abweichung(en), Sites {checks.Count}/{KnownEvidenceSiteCount}."
        };
    }

    private static IReadOnlyList<EvidenceSite> BuildDefinitions()
        =>
        [
            // NPCManager paths: both failure messages follow a type-4 allocation through
            // the central ShineObjectManager instance at 0x132826B8 -> som_AllocObject 0x54FE20.
            Evidence("NPCManager nm_SetNPC -> central allocator", "CENTRAL_ALLOCATOR_NPC", 0x004C6645,
                "6A048D4D9451B9B8262813E8CB970800",
                "Type 4 (ShineNPC) wird direkt an ShineObjectManager::som_AllocObject delegiert; kein lokales 256er Poollimit."),
            Evidence("NPCManager nm_DynamicRegenerateNPC -> central allocator", "CENTRAL_ALLOCATOR_NPC", 0x004C6EAC,
                "6A048D4DB051B9B8262813E8648F0800",
                "Dynamic-Regen delegiert Type 4 ebenfalls an som_AllocObject; 'Too many npc' folgt auf NULL."),

            // Representative Mob/related spawn paths all use the same allocator. The
            // MobBreeder block computes type 5 or 8 before the central call.
            Evidence("GuildTournament Mob spawn -> central allocator", "CENTRAL_ALLOCATOR_MOB", 0x0047C580,
                "6A058D45C450B9B8262813E890380D00",
                "Type 5 (ShineMob) wird über som_AllocObject erzeugt."),
            Evidence("MobBreeder regen -> central allocator", "CENTRAL_ALLOCATOR_MOB", 0x004B2D56,
                "8B5660F7DA1BD283E20383C205528D45AC50B9B8262813C745FC00000000E8A7D00900",
                "MobBreeder berechnet Type 5/8 und delegiert die Belegung an denselben ObjectManager."),
            Evidence("Pine SysFuncShineMobRegen -> central allocator", "CENTRAL_ALLOCATOR_MOB", 0x004E38B8,
                "6A058D55F052B9B8262813C745FC00000000E851C50600",
                "Pine Mob-Regen nutzt Type 5 via som_AllocObject."),
            Evidence("Pine SysFuncShineNPCStand -> central allocator", "CENTRAL_ALLOCATOR_NPC", 0x004E3B38,
                "6A048D55F052B9B8262813C745FC00000000E8D1C20600",
                "Pine NPC-Stand nutzt Type 4 via som_AllocObject."),
            Evidence("Pine ShineExchange2Mob -> central allocator", "CENTRAL_ALLOCATOR_MOB", 0x004ED74B,
                "6A058D85D4FEFFFF50B9B8262813E8C2260600",
                "Pine Exchange-to-Mob nutzt Type 5 via som_AllocObject."),

            // 0x100 look-alikes near object code are string-length limits: strlen-like
            // loop -> length -> compare <= 256 -> memcpy into packet buffer.
            Evidence("0x5610B9 is 256-byte string limit", "FALSE_POSITIVE_256", 0x005610B7,
                "2BC7B90001000066894602663BC1",
                "Die 256 begrenzt eine String-/Packetlänge und darf bei ShineNPC=512 nicht geändert werden."),
            Evidence("0x56115F is 256-byte string limit", "FALSE_POSITIVE_256", 0x0056115D,
                "2BC7B90001000066894602663BC1",
                "Zweite identische String-/Packetlängenprüfung; kein NPC-Poollimit."),

            // Player and Pet both have stock 1500. These exact blocks prove the second
            // 1500-family is ShinePet and must remain 1500 when only Player is enlarged.
            Evidence("Generic Pet 1500 + base 0x5C56", "PET_STOCK_INDEPENDENT", 0x00549046,
                "BADC050000663BD07709B8FFFF00005DC2040005565C00006689015DC2",
                "Generischer Pet-Handle-Erzeuger: Limit 1500, eigene Basis 0x5C56."),
            Evidence("Class Pet 1500 + base 0x5C56", "PET_STOCK_INDEPENDENT", 0x00557320,
                "B9DC050000663BC877178D4DFCBEFFFF0000E84901ECFF668BC65E8BE55DC2040005565C00008D4DFC6689",
                "Klassenspezifischer Pet-Handle-Erzeuger ist von ShinePlayer getrennt."),
            Evidence("Ctor Pet list +0x1CC uses 1500", "PET_STOCK_INDEPENDENT", 0x0055782C,
                "68DC0500008D8ECC010000",
                "ShineObjectManager-Konstruktor initialisiert die separate Pet-Liste +0x1CC mit 1500."),
            Evidence("som_Initialize Pet stride 0x25D4 uses 1500", "PET_STOCK_INDEPENDENT", 0x0055CEEB,
                "68DC0500008D780468D425000057C700DC050000",
                "Pet-Backing-Array: 1500 Elemente mit Pet-Stride 0x25D4; bei Player-Profilen unverändert lassen.")
        ];

    private static EvidenceSite Evidence(string name, string category, uint va, string expectedHex, string detail)
        => new(name, category, va, Convert.FromHexString(expectedHex), detail);

    private static ZonePoolControlFlowEvidenceCheck ToCheck(EvidenceSite site, int offset, byte[] actual, bool matches, string? actualText = null)
        => new()
        {
            Name = site.Name,
            Category = site.Category,
            Detail = site.Detail,
            VirtualAddress = site.VirtualAddress,
            FileOffset = offset,
            ExpectedHex = Convert.ToHexString(site.Expected),
            ActualHex = actualText ?? Convert.ToHexString(actual),
            Matches = matches
        };

    private static bool TryVirtualAddressToFileOffset(byte[] image, uint virtualAddress, out int fileOffset)
    {
        fileOffset = -1;
        try
        {
            if (image.Length < 0x100 || image[0] != (byte)'M' || image[1] != (byte)'Z') return false;
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
            if (peOffset < 0 || peOffset + 24 > image.Length) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(peOffset, 4)) != 0x00004550) return false;

            var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 6, 2));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 20, 2));
            var optionalOffset = peOffset + 24;
            if (optionalOffset + optionalSize > image.Length || optionalSize < 32) return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optionalOffset, 2)) != 0x10B) return false;
            var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optionalOffset + 28, 4));
            if (virtualAddress < imageBase) return false;
            var rva = virtualAddress - imageBase;

            var sectionOffset = optionalOffset + optionalSize;
            for (var i = 0; i < sectionCount; i++)
            {
                var sh = sectionOffset + i * 40;
                if (sh + 40 > image.Length) return false;
                var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 8, 4));
                var sectionRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 12, 4));
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 16, 4));
                var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 20, 4));
                var span = Math.Max(virtualSize, rawSize);
                if (rva < sectionRva || rva >= sectionRva + span) continue;
                var delta = rva - sectionRva;
                if (delta >= rawSize) return false;
                var offset = (long)rawOffset + delta;
                if (offset < 0 || offset > image.Length) return false;
                fileOffset = checked((int)offset);
                return true;
            }
        }
        catch { return false; }
        return false;
    }

    private static ZonePoolControlFlowAuditResult Failed(string detail, string hash = "")
        => new()
        {
            HashMatches = false,
            EvidenceVerified = false,
            BinarySha256 = hash,
            Detail = detail
        };

    private sealed record EvidenceSite(string Name, string Category, uint VirtualAddress, byte[] Expected, string Detail);
}

public sealed class ZonePoolControlFlowAuditResult
{
    public bool HashMatches { get; init; }
    public bool EvidenceVerified { get; init; }
    public string BinarySha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ZonePoolControlFlowEvidenceCheck> Sites { get; init; } = Array.Empty<ZonePoolControlFlowEvidenceCheck>();
}

public sealed class ZonePoolControlFlowEvidenceCheck
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public uint VirtualAddress { get; init; }
    public int FileOffset { get; init; }
    public string ExpectedHex { get; init; } = string.Empty;
    public string ActualHex { get; init; } = string.Empty;
    public bool Matches { get; init; }
}
