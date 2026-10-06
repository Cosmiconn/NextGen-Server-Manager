using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only, byte-exact preflight for the verified NA2016 Zone.exe.
/// This class never writes process memory or modifies a binary.
/// </summary>
public sealed class ZoneBinaryPatchPreflight
{
    public const int KnownCoreSiteCount = 69;

    public ZoneBinaryPatchPreflightResult Analyze(string zoneExePath, ZoneHandleRebasePlan targetLayout)
    {
        if (!targetLayout.IsValid)
            return Failed("Ziel-Handlelayout ist ungültig: " + targetLayout.Detail, layoutValid: false);
        if (string.IsNullOrWhiteSpace(zoneExePath) || !File.Exists(zoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.");

        byte[] image;
        try { image = File.ReadAllBytes(zoneExePath); }
        catch (Exception ex) { return Failed("Zone.exe konnte nicht gelesen werden: " + ex.Message); }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        var hashOk = hash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase);
        if (!hashOk)
            return Failed("Zone.exe-Hash weicht vom verifizierten NA2016-Build ab. Adressgebundener Preflight abgebrochen.", hash);

        IReadOnlyList<PatchSiteDefinition> definitions;
        try { definitions = BuildCoreSiteDefinitions(targetLayout); }
        catch (Exception ex) { return Failed("Patchmatrix konnte nicht aufgebaut werden: " + ex.Message, hash, true); }

        var checks = new List<ZoneBinaryPatchSiteCheck>(definitions.Count);
        foreach (var site in definitions)
        {
            if (!TryVirtualAddressToFileOffset(image, site.VirtualAddress, out var fileOffset))
            {
                checks.Add(Check(site, -1, Array.Empty<byte>(), false, "VA NICHT ABBILDBAR"));
                continue;
            }

            var available = Math.Max(0, Math.Min(site.Expected.Length, image.Length - fileOffset));
            var actual = image.AsSpan(fileOffset, available).ToArray();
            var matches = available == site.Expected.Length && actual.AsSpan().SequenceEqual(site.Expected);
            checks.Add(Check(site, fileOffset, actual, matches));
        }

        var countOk = definitions.Count == KnownCoreSiteCount;
        var coreOk = countOk && checks.All(x => x.Matches);
        var failed = checks.Count(x => !x.Matches) + (countOk ? 0 : 1);
        var changed = checks.Count(x => x.WouldChange);

        // Intentionally false until every remaining occurrence of stock handle/pool constants
        // outside these proven core functions has been semantically classified.
        const bool coverageComplete = false;

        return new ZoneBinaryPatchPreflightResult
        {
            HashMatches = true,
            LayoutValid = true,
            CoreSitesVerified = coreOk,
            CoverageComplete = coverageComplete,
            BinarySha256 = hash,
            Sites = checks,
            Detail = coreOk
                ? $"Kern-Preflight OK: {checks.Count}/{KnownCoreSiteCount} adressgebundene Stock-Instruktionen verifiziert; {changed} würden sich für das Zielprofil ändern. Vollabdeckung bleibt gesperrt."
                : $"Kern-Preflight FEHLGESCHLAGEN: {failed} Abweichung(en), Sites {checks.Count}/{KnownCoreSiteCount}. Kein Patch zulässig."
        };
    }

    private static ZoneBinaryPatchSiteCheck Check(PatchSiteDefinition site, int offset, byte[] actual, bool matches, string? actualText = null)
        => new()
        {
            Name = site.Name,
            Category = site.Category,
            VirtualAddress = site.VirtualAddress,
            FileOffset = offset,
            ExpectedHex = Convert.ToHexString(site.Expected),
            ActualHex = actualText ?? Convert.ToHexString(actual),
            ReplacementHex = Convert.ToHexString(site.Replacement),
            Matches = matches
        };

    private static IReadOnlyList<PatchSiteDefinition> BuildCoreSiteDefinitions(ZoneHandleRebasePlan target)
    {
        var stock = ZoneHandleLayout.StockPlan;
        if (!stock.IsValid) throw new InvalidOperationException("Interne Stock-Handlematrix ist ungültig.");

        ZoneHandleRange S(string name) => stock.Ranges.Single(x => x.Name == name);
        ZoneHandleRange T(string name) => target.Ranges.Single(x => x.Name == name);
        var sites = new List<PatchSiteDefinition>(KnownCoreSiteCount);

        // ShineObjectManager constructor maxima.
        sites.Add(Imm32("Ctor ShinePlayer max", "POOL_INIT", 0x00557622, 0x68, S("ShinePlayer").Capacity, T("ShinePlayer").Capacity));
        sites.Add(Imm32("Ctor ShineNPC max", "POOL_INIT", 0x0055765C, 0x68, S("ShineNPC").Capacity, T("ShineNPC").Capacity));
        sites.Add(Imm32("Ctor ShineMob max", "POOL_INIT", 0x005576D0, 0x68, S("ShineMob").Capacity, T("ShineMob").Capacity));

        // Generic handle creators: mutable limits.
        sites.Add(Imm32("Generic Mob limit", "HANDLE_CREATE_LIMIT", 0x00548E07, 0xBA, S("ShineMob").Capacity, T("ShineMob").Capacity));
        sites.Add(Imm32("Generic Player limit", "HANDLE_CREATE_LIMIT", 0x00548E36, 0xBA, S("ShinePlayer").Capacity, T("ShinePlayer").Capacity));
        sites.Add(Imm32("Generic NPC limit", "HANDLE_CREATE_LIMIT", 0x00548F26, 0xBA, S("ShineNPC").Capacity, T("ShineNPC").Capacity));

        foreach (var (name, va, range) in GenericBases)
            sites.Add(Imm32($"Generic {name} base", "HANDLE_CREATE_BASE", va, 0x05, S(range).Start, T(range).Start));

        // Independently compiled class-specific handle creators.
        sites.Add(Imm32("Class Mob limit", "CLASS_HANDLE_LIMIT", 0x00556A51, 0xB9, S("ShineMob").Capacity, T("ShineMob").Capacity));
        sites.Add(Imm32("Class Player limit", "CLASS_HANDLE_LIMIT", 0x00559D50, 0xB9, S("ShinePlayer").Capacity, T("ShinePlayer").Capacity));
        sites.Add(Imm32("Class NPC limit", "CLASS_HANDLE_LIMIT", 0x00557C80, 0xB9, S("ShineNPC").Capacity, T("ShineNPC").Capacity));
        foreach (var (name, va, range) in ClassBases)
            sites.Add(Imm32($"Class {name} base", "CLASS_HANDLE_BASE", va, 0x05, S(range).Start, T(range).Start));

        // Central ShineObjectHandleUnion::sohu_HandleSplit.
        foreach (var (name, va, opcode, range) in SplitBoundaries)
            sites.Add(Imm32("HandleSplit " + name, "HANDLE_SPLIT_BOUNDARY", va, opcode, S(range).Start, T(range).Start));
        sites.Add(Imm32("HandleSplit Pet end / first unused", "HANDLE_SPLIT_BOUNDARY", 0x00633803, 0xB9, stock.FirstUnusedHandle, target.FirstUnusedHandle));
        foreach (var (name, va, range) in SplitSubtracts)
            sites.Add(Imm32("HandleSplit " + name, "HANDLE_SPLIT_LOCALIZE", va, 0x05, unchecked(-S(range).Start), unchecked(-T(range).Start)));

        return sites;
    }

    private static readonly (string Name, uint Va, string Range)[] GenericBases =
    [
        ("Player", 0x00548E49, "ShinePlayer"),
        ("Effect", 0x00548E79, "ShineEffectObject"),
        ("DropItem", 0x00548EA9, "ShineDropItem"),
        ("AxialFlag", 0x00548ED9, "ShineAxialFlag"),
        ("Bandit", 0x00548F09, "ShineBandit"),
        ("NPC", 0x00548F39, "ShineNPC"),
        ("MiniHouse", 0x00548F69, "ShineMiniHouse"),
        ("MagicField", 0x00548F99, "ShineMagicField"),
        ("Door", 0x00548FC9, "ShineDoor"),
        ("Servant", 0x00548FF9, "ShineServant"),
        ("Mover", 0x00549029, "ShineMover"),
        ("Pet", 0x00549059, "ShinePet")
    ];

    private static readonly (string Name, uint Va, string Range)[] ClassBases =
    [
        ("Player", 0x00559D71, "ShinePlayer"),
        ("Effect", 0x00555E91, "ShineEffectObject"),
        ("DropItem", 0x005552F1, "ShineDropItem"),
        ("AxialFlag", 0x005562F1, "ShineAxialFlag"),
        ("NPC", 0x00557CA1, "ShineNPC"),
        ("Bandit", 0x00556F01, "ShineBandit"),
        ("MiniHouse", 0x00555481, "ShineMiniHouse"),
        ("MagicField", 0x00555CD1, "ShineMagicField"),
        ("Door", 0x00555BD1, "ShineDoor"),
        ("Servant", 0x00557071, "ShineServant"),
        ("Mover", 0x0055EE21, "ShineMover"),
        ("Pet", 0x00557341, "ShinePet")
    ];

    private static readonly (string Name, uint Va, byte Opcode, string Range)[] SplitBoundaries =
    [
        ("Mob end / Player start", 0x00633656, 0xB9, "ShinePlayer"),
        ("Player end", 0x0063366C, 0xBA, "ShineEffectObject"),
        ("Effect start", 0x00633687, 0xBA, "ShineEffectObject"),
        ("Effect end", 0x00633691, 0xB9, "ShineDropItem"),
        ("Drop start", 0x006336AC, 0xB9, "ShineDropItem"),
        ("Drop end", 0x006336B6, 0xBA, "ShineAxialFlag"),
        ("Axial start", 0x006336D1, 0xBA, "ShineAxialFlag"),
        ("Axial end", 0x006336DB, 0xB9, "ShineNPC"),
        ("NPC start", 0x006336F6, 0xB9, "ShineNPC"),
        ("NPC end", 0x00633700, 0xBA, "ShineBandit"),
        ("Bandit start", 0x0063371B, 0xBA, "ShineBandit"),
        ("Bandit end", 0x00633725, 0xB9, "ShineMiniHouse"),
        ("MiniHouse start", 0x00633740, 0xB9, "ShineMiniHouse"),
        ("MiniHouse end", 0x0063374A, 0xBA, "ShineMagicField"),
        ("MagicField start", 0x00633765, 0xBA, "ShineMagicField"),
        ("MagicField end", 0x0063376F, 0xB9, "ShineDoor"),
        ("Door start", 0x0063378A, 0xB9, "ShineDoor"),
        ("Door end", 0x00633794, 0xBA, "ShineServant"),
        ("Servant start", 0x006337AF, 0xBA, "ShineServant"),
        ("Servant end", 0x006337B9, 0xB9, "ShineMover"),
        ("Mover start", 0x006337D4, 0xB9, "ShineMover"),
        ("Mover end / reserved start", 0x006337DE, 0xBA, "RESERVED_INVALID"),
        ("Pet start after reserved gap", 0x006337F9, 0xBA, "ShinePet")
    ];

    private static readonly (string Name, uint Va, string Range)[] SplitSubtracts =
    [
        ("Player local index", 0x00633679, "ShinePlayer"),
        ("Effect local index", 0x0063369E, "ShineEffectObject"),
        ("Drop local index", 0x006336C3, "ShineDropItem"),
        ("Axial local index", 0x006336E8, "ShineAxialFlag"),
        ("NPC local index", 0x0063370D, "ShineNPC"),
        ("Bandit local index", 0x00633732, "ShineBandit"),
        ("MiniHouse local index", 0x00633757, "ShineMiniHouse"),
        ("MagicField local index", 0x0063377C, "ShineMagicField"),
        ("Door local index", 0x006337A1, "ShineDoor"),
        ("Servant local index", 0x006337C6, "ShineServant"),
        ("Mover local index", 0x006337EB, "ShineMover"),
        ("Pet local index", 0x00633810, "ShinePet")
    ];

    private static PatchSiteDefinition Imm32(string name, string category, uint va, byte opcode, int stockValue, int targetValue)
        => new(name, category, va, EncodeInstruction(opcode, stockValue), EncodeInstruction(opcode, targetValue));

    private static byte[] EncodeInstruction(byte opcode, int value)
    {
        var bytes = new byte[5];
        bytes[0] = opcode;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), value);
        return bytes;
    }

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

    private static ZoneBinaryPatchPreflightResult Failed(string detail, string hash = "", bool hashMatches = false, bool layoutValid = true)
        => new()
        {
            HashMatches = hashMatches,
            LayoutValid = layoutValid,
            CoreSitesVerified = false,
            CoverageComplete = false,
            BinarySha256 = hash,
            Detail = detail
        };

    private sealed record PatchSiteDefinition(string Name, string Category, uint VirtualAddress, byte[] Expected, byte[] Replacement);
}

public sealed class ZoneBinaryPatchPreflightResult
{
    public bool HashMatches { get; init; }
    public bool LayoutValid { get; init; }
    public bool CoreSitesVerified { get; init; }
    public bool CoverageComplete { get; init; }
    public bool CanPatch => HashMatches && LayoutValid && CoreSitesVerified && CoverageComplete;
    public string BinarySha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ZoneBinaryPatchSiteCheck> Sites { get; init; } = Array.Empty<ZoneBinaryPatchSiteCheck>();
}

public sealed class ZoneBinaryPatchSiteCheck
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public uint VirtualAddress { get; init; }
    public int FileOffset { get; init; }
    public string ExpectedHex { get; init; } = string.Empty;
    public string ActualHex { get; init; } = string.Empty;
    public string ReplacementHex { get; init; } = string.Empty;
    public bool Matches { get; init; }
    public bool WouldChange => ExpectedHex != ReplacementHex;
}
