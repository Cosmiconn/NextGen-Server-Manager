using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only, hash-bound inventory of every direct x86 CALL to
/// ShineObjectManager::som_AllocObject in the verified NA2016 Zone.exe.
/// This closes a blind spot left by representative control-flow samples:
/// if a direct allocator caller is added, removed or moved, the audit fails.
/// </summary>
public sealed class ZoneAllocatorCallInventoryAudit
{
    private const uint SomAllocObjectVa = 0x0054FE20;

    private static readonly uint[] KnownDirectCalls =
    [
        0x00408E0B, 0x0041AAF8, 0x0045DA62, 0x0045ED2C, 0x00464D85, 0x00464D98, 0x00464E40,
        0x0047C58B, 0x004A767F, 0x004B0A91, 0x004B2D74, 0x004C6650, 0x004C6EB7, 0x004D2688,
        0x004D2A96, 0x004E38CA, 0x004E3A0A, 0x004E3B4A, 0x004E3C8A, 0x004E3DCA, 0x004E3F0A,
        0x004ED2B3, 0x004ED3F4, 0x004ED597, 0x004ED759, 0x004F27CE, 0x004F2BFE, 0x004F938F,
        0x004F94E4, 0x004F9654, 0x0050DE1F, 0x00528326, 0x00529D1F, 0x0052AC3C, 0x005765B6,
        0x0057C972, 0x0057C9CE, 0x005AA937, 0x005D855B, 0x005D87BB, 0x005DC6D0, 0x005E1FE3,
        0x005E226F, 0x005E506B, 0x005E7823, 0x005F0C64, 0x005F193B
    ];

    private static readonly HashSet<uint> DirectNpcType4Calls =
    [
        0x004C6650, 0x004C6EB7, 0x004E3B4A, 0x004E3F0A, 0x004ED597, 0x004F9654
    ];

    private static readonly HashSet<uint> DirectMobType5Calls =
    [
        0x0047C58B, 0x004E38CA, 0x004E3C8A, 0x004ED2B3, 0x004ED759,
        0x005D855B, 0x005D87BB, 0x005E1FE3, 0x005E226F, 0x005E7823
    ];

    // MobBreeder computes Type 5 or 8 immediately before this direct allocator call.
    private const uint DynamicMobBanditCall = 0x004B2D74;

    public ZoneAllocatorCallInventoryAuditResult Analyze(string zoneExePath)
    {
        if (string.IsNullOrWhiteSpace(zoneExePath) || !File.Exists(zoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.");

        byte[] image;
        try { image = File.ReadAllBytes(zoneExePath); }
        catch (Exception ex) { return Failed("Zone.exe konnte nicht gelesen werden: " + ex.Message); }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (!hash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Zone.exe-Hash weicht vom verifizierten NA2016-Build ab.", hash);

        if (!TryReadPe(image, out var pe))
            return Failed("PE-Header oder ausführbare Sections konnten nicht gelesen werden.", hash);

        var actual = new List<uint>();
        foreach (var section in pe.Sections.Where(x => x.Executable))
        {
            var max = Math.Min(section.RawSize, image.Length - section.RawOffset);
            for (var i = 0; i <= max - 5; i++)
            {
                var fileOffset = section.RawOffset + i;
                if (image[fileOffset] != 0xE8)
                    continue;

                var rel = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(fileOffset + 1, 4));
                var callVa = checked(pe.ImageBase + section.VirtualAddress + (uint)i);
                var target = unchecked((uint)((long)callVa + 5L + rel));
                if (target == SomAllocObjectVa)
                    actual.Add(callVa);
            }
        }

        actual.Sort();
        var expected = KnownDirectCalls.OrderBy(x => x).ToArray();
        var exact = actual.SequenceEqual(expected);
        var missing = expected.Except(actual).OrderBy(x => x).ToArray();
        var unexpected = actual.Except(expected).OrderBy(x => x).ToArray();

        var npcSeen = actual.Count(DirectNpcType4Calls.Contains);
        var mobSeen = actual.Count(DirectMobType5Calls.Contains);
        var dynamicSeen = actual.Contains(DynamicMobBanditCall);

        return new ZoneAllocatorCallInventoryAuditResult
        {
            HashMatches = true,
            InventoryMatches = exact,
            BinarySha256 = hash,
            ExpectedDirectCallCount = expected.Length,
            ActualDirectCallCount = actual.Count,
            DirectNpcType4CallCount = npcSeen,
            DirectMobType5CallCount = mobSeen,
            DynamicMobBanditCallPresent = dynamicSeen,
            ActualCallSites = actual,
            MissingCallSites = missing,
            UnexpectedCallSites = unexpected,
            Detail = exact
                ? $"Allocator-Inventur OK: {actual.Count} direkte som_AllocObject-CALLs vollständig belegt; davon {npcSeen} literal NPC(Type 4), {mobSeen} literal Mob(Type 5) und MobBreeder Type 5/8={(dynamicSeen ? "belegt" : "FEHLT")}. Die übrigen direkten Calls bleiben typ-/pfadspezifisch zu klassifizieren."
                : $"Allocator-Inventur ABWEICHEND: erwartet {expected.Length}, gefunden {actual.Count}; fehlend {missing.Length}, unerwartet {unexpected.Length}. CoverageComplete bleibt gesperrt."
        };
    }

    private static bool TryReadPe(byte[] image, out PeInfo pe)
    {
        pe = default;
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

            var sections = new List<PeSection>(sectionCount);
            var sectionOffset = optionalOffset + optionalSize;
            for (var i = 0; i < sectionCount; i++)
            {
                var sh = sectionOffset + i * 40;
                if (sh + 40 > image.Length) return false;
                var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 12, 4));
                var rawSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 16, 4)));
                var rawOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 20, 4)));
                var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 36, 4));
                if (rawOffset < 0 || rawOffset > image.Length) return false;
                sections.Add(new PeSection(virtualAddress, rawOffset, rawSize, (characteristics & 0x20000000) != 0));
            }

            pe = new PeInfo(imageBase, sections);
            return true;
        }
        catch { return false; }
    }

    private static ZoneAllocatorCallInventoryAuditResult Failed(string detail, string hash = "")
        => new()
        {
            HashMatches = !string.IsNullOrEmpty(hash),
            InventoryMatches = false,
            BinarySha256 = hash,
            Detail = detail
        };

    private readonly record struct PeInfo(uint ImageBase, IReadOnlyList<PeSection> Sections);
    private readonly record struct PeSection(uint VirtualAddress, int RawOffset, int RawSize, bool Executable);
}

public sealed class ZoneAllocatorCallInventoryAuditResult
{
    public bool HashMatches { get; init; }
    public bool InventoryMatches { get; init; }
    public string BinarySha256 { get; init; } = string.Empty;
    public int ExpectedDirectCallCount { get; init; }
    public int ActualDirectCallCount { get; init; }
    public int DirectNpcType4CallCount { get; init; }
    public int DirectMobType5CallCount { get; init; }
    public bool DynamicMobBanditCallPresent { get; init; }
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<uint> ActualCallSites { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> MissingCallSites { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> UnexpectedCallSites { get; init; } = Array.Empty<uint>();
}
