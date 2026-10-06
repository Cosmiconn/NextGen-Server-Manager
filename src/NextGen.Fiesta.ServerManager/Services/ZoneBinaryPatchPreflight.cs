using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only byte-exact preflight for the verified NA2016 Zone.exe.
/// This service never modifies a binary. It proves that known core patch sites still
/// contain their expected stock instructions before any future writer is even considered.
/// </summary>
public sealed class ZoneBinaryPatchPreflight
{
    public ZoneBinaryPatchPreflightResult Analyze(string zoneExePath, ZoneHandleRebasePlan targetLayout)
    {
        if (!targetLayout.IsValid)
            return Failed("Ziel-Handlelayout ist ungültig: " + targetLayout.Detail, layoutValid: false);
        if (string.IsNullOrWhiteSpace(zoneExePath) || !File.Exists(zoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.");

        byte[] image;
        try
        {
            image = File.ReadAllBytes(zoneExePath);
        }
        catch (Exception ex)
        {
            return Failed("Zone.exe konnte nicht gelesen werden: " + ex.Message);
        }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        var hashOk = hash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase);
        if (!hashOk)
        {
            return new ZoneBinaryPatchPreflightResult
            {
                HashMatches = false,
                LayoutValid = true,
                CoreSitesVerified = false,
                CoverageComplete = false,
                BinarySha256 = hash,
                Detail = "Zone.exe-Hash weicht vom verifizierten NA2016-Build ab. Adressgebundener Preflight abgebrochen."
            };
        }

        IReadOnlyList<PatchSiteDefinition> definitions;
        try
        {
            definitions = BuildCoreSiteDefinitions(targetLayout);
        }
        catch (Exception ex)
        {
            return Failed("Patchmatrix konnte nicht aufgebaut werden: " + ex.Message, hash, hashOk, true);
        }

        var checks = new List<ZoneBinaryPatchSiteCheck>(definitions.Count);
        foreach (var site in definitions)
        {
            if (!TryVirtualAddressToFileOffset(image, site.VirtualAddress, out var fileOffset))
            {
                checks.Add(new ZoneBinaryPatchSiteCheck
                {
                    Name = site.Name,
                    Category = site.Category,
                    VirtualAddress = site.VirtualAddress,
                    FileOffset = -1,
                    ExpectedHex = Convert.ToHexString(site.Expected),
                    ReplacementHex = Convert.ToHexString(site.Replacement),
                    ActualHex = "VA NICHT ABBILDBAR",
                    Matches = false
                });
                continue;
            }

            var available = Math.Max(0, Math.Min(site.Expected.Length, image.Length - fileOffset));
            var actual = available == site.Expected.Length
                ? image.AsSpan(fileOffset, site.Expected.Length).ToArray()
                : image.AsSpan(fileOffset, available).ToArray();
            var matches = actual.AsSpan().SequenceEqual(site.Expected);
            checks.Add(new ZoneBinaryPatchSiteCheck
            {
                Name = site.Name,
                Category = site.Category,
                VirtualAddress = site.VirtualAddress,
                FileOffset = fileOffset,
                ExpectedHex = Convert.ToHexString(site.Expected),
                ReplacementHex = Convert.ToHexString(site.Replacement),
                ActualHex = Convert.ToHexString(actual),
                Matches = matches
            });
        }

        var coreOk = checks.Count > 0 && checks.All(x => x.Matches);
        var failed = checks.Count(x => !x.Matches);
        var changed = checks.Count(x => x.ExpectedHex != x.ReplacementHex);

        // Deliberately false. The core sites below are proven, but there are additional
        // occurrences of stock constants elsewhere in Zone.exe that still need semantic
        // classification as true handle/pool dependencies or unrelated structure offsets.
        // A future writer MUST NOT use this preflight until that audit closes the coverage gap.
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
                ? $"Kern-Preflight OK: {checks.Count} adressgebundene Stock-Instruktionen verifiziert, {changed} würden sich für das Zielprofil ändern. Vollabdeckung bleibt gesperrt, bis alle weiteren Konstantenreferenzen klassifiziert sind."
                : $"Kern-Preflight FEHLGESCHLAGEN: {failed}/{checks.Count} erwartete Instruktionen stimmen nicht exakt. Kein Patch zulässig."
        };
    }

    private static IReadOnlyList<PatchSiteDefinition> BuildCoreSiteDefinitions(ZoneHandleRebasePlan target)
    {
        var stock = ZoneHandleLayout.StockPlan;
        if (!stock.IsValid)
            throw new InvalidOperationException("Interne Stock-Handlematrix ist ungültig.");

        ZoneHandleRange S(string name) => stock.Ranges.Single(x => x.Name == name);
        ZoneHandleRange T(string name) => target.Ranges.Single(x => x.Name == name);

        var sites = new List<PatchSiteDefinition>();

        // Pool maxima passed by ShineObjectManager constructor.
        sites.Add(Imm32("Ctor ShinePlayer max", "POOL_INIT", 0x00557622, 0x68, S("ShinePlayer").Capacity, T("ShinePlayer").Capacity));
        sites.Add(Imm32("Ctor ShineNPC max", "POOL_INIT", 0x0055765C, 0x68, S("ShineNPC").Capacity, T("ShineNPC").Capacity));
        sites.Add(Imm32("Ctor ShineMob max", "POOL_INIT", 0x005576D0, 0x68, S("ShineMob").Capacity, T("ShineMob").Capacity));

        // Generic handle creators: changed pool limits.
        sites.Add(Imm32("Generic Mob limit", "HANDLE_CREATE_LIMIT", 0x00548E07, 0xBA, S("ShineMob").Capacity, T("ShineMob").Capacity));
        sites.Add(Imm32("Generic Player limit", "HANDLE_CREATE_LIMIT", 0x00548E36, 0xBA, S("ShinePlayer").Capacity, T("ShinePlayer").Capacity));
        sites.Add(Imm32("Generic NPC limit", "HANDLE_CREATE_LIMIT", 0x00548F26, 0xBA, S("ShineNPC").Capacity, T("ShineNPC").Capacity));

        // Generic handle creators: every non-zero base that can move after Mob/Player/NPC changes.
        AddGenericBase(sites, "Player", 0x00548E49, S("ShinePlayer").Start, T("ShinePlayer").Start);
        AddGenericBase(sites, "Effect", 0x00548E79, S("ShineEffectObject").Start, T("ShineEffectObject").Start);
        AddGenericBase(sites, "DropItem", 0x00548EA9, S("ShineDropItem").Start, T("ShineDropItem").Start);
        AddGenericBase(sites, "AxialFlag", 0x00548ED9, S("ShineAxialFlag").Start, T("ShineAxialFlag").Start);
        AddGenericBase(sites, "Bandit", 0x00548F09, S("ShineBandit").Start, T("ShineBandit").Start);
        AddGenericBase(sites, "NPC", 0x00548F39, S("ShineNPC").Start, T("ShineNPC").Start);
        AddGenericBase(sites, "MiniHouse", 0x00548F69, S("ShineMiniHouse").Start, T("ShineMiniHouse").Start);
        AddGenericBase(sites, "MagicField", 0x00548F99, S("ShineMagicField").Start, T("ShineMagicField").Start);
        AddGenericBase(sites, "Door", 0x00548FC9, S("ShineDoor").Start, T("ShineDoor").Start);
        AddGenericBase(sites, "Servant", 0x00548FF9, S("ShineServant").Start, T("ShineServant").Start);
        AddGenericBase(sites, "Mover", 0x00549029, S("ShineMover").Start, T("ShineMover").Start);
        AddGenericBase(sites, "Pet", 0x00549059, S("ShinePet").Start, T("ShinePet").Start);

        // Class-specific handle creators verified independently of the generic table.
        sites.Add(Imm32("Class Mob limit", "CLASS_HANDLE_LIMIT", 0x00556A51, 0xB9, S("ShineMob").Capacity, T("ShineMob").Capacity));
        sites.Add(Imm32("Class Player limit", "CLASS_HANDLE_LIMIT", 0x00559D50, 0xB9, S("ShinePlayer").Capacity, T("ShinePlayer").Capacity));
        sites.Add(Imm32("Class NPC limit", "CLASS_HANDLE_LIMIT", 0x00557C80, 0xB9, S("ShineNPC").Capacity, T("ShineNPC").Capacity));
        AddClassBase(sites, "Player", 0x00559D71, S("ShinePlayer").Start, T("ShinePlayer").Start);
        AddClassBase(sites, "Effect", 0x00555E91, S("ShineEffectObject").Start, T("ShineEffectObject").Start);
        AddClassBase(sites, "DropItem", 0x005552F1, S("ShineDropItem").Start, T("ShineDropItem").Start);
        AddClassBase(sites, "AxialFlag", 0x005562F1, S("ShineAxialFlag").Start, T("ShineAxialFlag").Start);
        AddClassBase(sites, "NPC", 0x00557CA1, S("ShineNPC").Start, T("ShineNPC").Start);
        AddClassBase(sites, "Bandit", 0x00556F01, S("ShineBandit").Start, T("ShineBandit").Start);
        AddClassBase(sites, "MiniHouse", 0x00555481, S("ShineMiniHouse").Start, T("ShineMiniHouse").Start);
        AddClassBase(sites, "MagicField", 0x00555CD1, S("ShineMagicField").Start, T("ShineMagicField").Start);
        AddClassBase(sites, "Door", 0x00555BD1, S("ShineDoor").Start, T("ShineDoor").Start);
        AddClassBase(sites, "Servant", 0x00557071, S("ShineServant").Start, T("ShineServant").Start);
        AddClassBase(sites, "Mover", 0x0055EE21, S("ShineMover").Start, T("ShineMover").Start);
        AddClassBase(sites, "Pet", 0x00557341, S("ShinePet").Start, T("ShinePet").Start);

        // Central ShineObjectHandleUnion::sohu_HandleSplit boundaries.
        AddSplitBoundary(sites, "Mob end / Player start", 0x00633656, 0xB9, S("ShinePlayer").Start, T("ShinePlayer").Start);
        AddSplitBoundary(sites, "Player end", 0x0063366C, 0xBA, S("ShineEffectObject").Start, T("ShineEffectObject").Start);
        AddSplitSubtract(sites, "Player local index", 0x00633679, S("ShinePlayer").Start, T("ShinePlayer").Start);
        AddSplitBoundary(sites, "Effect start", 0x00633687, 0xBA, S("ShineEffectObject").Start, T("ShineEffectObject").Start);
        AddSplitBoundary(sites, "Effect end", 0x00633691, 0xB9, S("ShineDropItem").Start, T("ShineDropItem").Start);
        AddSplitSubtract(sites, "Effect local index", 0x0063369E, S("ShineEffectObject").Start, T("ShineEffectObject").Start);
        AddSplitBoundary(sites, "Drop start", 0x006336AC, 0xB9, S("ShineDropItem").Start, T("ShineDropItem").Start);
        AddSplitBoundary(sites, "Drop end", 0x006336B6, 0xBA, S("ShineAxialFlag").Start, T("ShineAxialFlag").Start);
        AddSplitSubtract(sites, "Drop local index", 0x006336C3, S("ShineDropItem").Start, T("ShineDropItem").Start);
        AddSplitBoundary(sites, "Axial start", 0x006336D1, 0xBA, S("ShineAxialFlag").Start, T("ShineAxialFlag").Start);
        AddSplitBoundary(sites, "Axial end", 0x006336DB, 0xB9, S("ShineNPC").Start, T("ShineNPC").Start);
        AddSplitSubtract(sites, "Axial local index", 0x006336E8, S("ShineAxialFlag").Start, T("ShineAxialFlag").Start);
        AddSplitBoundary(sites, "NPC start", 0x006336F6, 0xB9, S("ShineNPC").Start, T("ShineNPC").Start);
        AddSplitBoundary(sites, "NPC end", 0x00633700, 0xBA, S("ShineBandit").Start, T("ShineBandit").Start);
        AddSplitSubtract(sites, "NPC local index", 0x0063370D, S("ShineNPC").Start, T("ShineNPC").Start);
        AddSplitBoundary(sites, "Bandit start", 0x0063371B, 0xBA, S("ShineBandit").Start, T("ShineBandit").Start);
        AddSplitBoundary(sites, "Bandit end", 0x00633725, 0xB9, S("ShineMiniHouse").Start, T("ShineMiniHouse").Start);
        AddSplitSubtract(sites, "Bandit local index", 0x00633732, S("ShineBandit").Start, T("ShineBandit").Start);
        AddSplitBoundary(sites, "MiniHouse start", 0x00633740, 0xB9, S("ShineMiniHouse").Start, T("ShineMiniHouse").Start);
        AddSplitBoundary(sites, "MiniHouse end", 0x0063374A, 0xBA, S("ShineMagicField").Start, T("ShineMagicField").Start);
        AddSplitSubtract(sites, "MiniHouse local index", 0x00633757, S("ShineMiniHouse").Start, T("ShineMiniHouse").Start);
        AddSplitBoundary(sites, "MagicField start", 0x00633765, 0xBA, S("ShineMagicField").Start, T("ShineMagicField").Start);
        AddSplitBoundary(sites, "MagicField end", 0x0063376F, 0xB9, S("ShineDoor").Start, T("ShineDoor").Start);
        AddSplitSubtract(sites, "MagicField local index", 0x0063377C, S("ShineMagicField").Start, T("ShineMagicField").Start);
        AddSplitBoundary(sites, "Door start", 0x0063378A, 0xB9, S("ShineDoor").Start, T("ShineDoor").Start);
        AddSplitBoundary(sites, "Door end", 0x00633794, 0xBA, S("ShineServant").Start, T("ShineServant").Start);
        AddSplitSubtract(sites, "Door local index", 0x006337A1, S("ShineDoor").Start, T("ShineDoor").Start);
        AddSplitBoundary(sites, "Servant start", 0x006337AF, 0xBA, S("ShineServant").Start, T("ShineServant").Start);
        AddSplitBoundary(sites, "Servant end", 0x006337B9, 0xB9, S("ShineMover").Start, T("ShineMover").Start);
        AddSplitSubtract(sites, "Servant local index", 0x006337C6, S("ShineServant").Start, T("ShineServant").Start);
        AddSplitBoundary(sites, "Mover start", 0x006337D4, 0xB9, S("ShineMover").Start, T("ShineMover").Start);
        AddSplitBoundary(sites, "Mover end / reserved start", 0x006337DE, 0xBA, S("RESERVED_INVALID").Start, T("RESERVED_INVALID").Start);
        AddSplitSubtract(sites, "Mover local index", 0x006337EB, S("ShineMover").Start, T("ShineMover").Start);
        AddSplitBoundary(sites, "Pet start after reserved gap", 0x006337F9, 0xBA, S("ShinePet").Start, T("ShinePet").Start);
        AddSplitBoundary(sites, "Pet end / first unused", 0x00633803, 0xB9, stock.FirstUnusedHandle, target.FirstUnusedHandle);
        AddSplitSubtract(sites, "Pet local index", 0x00633810, S("ShinePet").Start, T("ShinePet").Start);

        return sites;
    }

    private static void AddGenericBase(List<PatchSiteDefinition> sites, string name, uint va, int stock, int target)
        => sites.Add(Imm32($"Generic {name} base", "HANDLE_CREATE_BASE", va, 0x05, stock, target));

    private static void AddClassBase(List<PatchSiteDefinition> sites, string name, uint va, int stock, int target)
        => sites.Add(Imm32($"Class {name} base", "CLASS_HANDLE_BASE", va, 0x05, stock, target));

    private static void AddSplitBoundary(List<PatchSiteDefinition> sites, string name, uint va, byte opcode, int stock, int target)
        => sites.Add(Imm32("HandleSplit " + name, "HANDLE_SPLIT_BOUNDARY", va, opcode, stock, target));

    private static void AddSplitSubtract(List<PatchSiteDefinition> sites, string name, uint va, int stockBase, int targetBase)
        => sites.Add(Imm32("HandleSplit " + name, "HANDLE_SPLIT_LOCALIZE", va, 0x05, unchecked(-stockBase), unchecked(-targetBase))));

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
            if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optionalOffset, 2)) != 0x10B) return false; // PE32
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
        catch
        {
            return false;
        }
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
