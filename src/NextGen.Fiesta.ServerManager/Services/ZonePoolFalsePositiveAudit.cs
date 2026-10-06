using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only, hash-bound proof for high-risk numeric look-alikes in Zone.exe.
/// These values equal stock Player/Mob/NPC capacities but have independent semantics
/// and therefore MUST NOT be changed by a Player/Mob/NPC rebase profile.
/// </summary>
public sealed class ZonePoolFalsePositiveAudit
{
    public const int KnownFalsePositiveSiteCount = 7;

    public ZonePoolFalsePositiveAuditResult Analyze(string zoneExePath)
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
        var checks = new List<ZonePoolFalsePositiveCheck>(definitions.Count);
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

        var countOk = definitions.Count == KnownFalsePositiveSiteCount;
        var verified = countOk && checks.All(x => x.Matches);
        var failed = checks.Count(x => !x.Matches) + (countOk ? 0 : 1);
        return new ZonePoolFalsePositiveAuditResult
        {
            HashMatches = true,
            SitesVerified = verified,
            BinarySha256 = hash,
            Sites = checks,
            Detail = verified
                ? $"False-Positive-Audit OK: {checks.Count}/{KnownFalsePositiveSiteCount} gefährliche Zahlen-Doppelgänger bytegenau verifiziert und explizit vom Pool-Patch ausgeschlossen."
                : $"False-Positive-Audit FEHLGESCHLAGEN: {failed} Abweichung(en), Sites {checks.Count}/{KnownFalsePositiveSiteCount}."
        };
    }

    private static IReadOnlyList<FalsePositiveSite> BuildDefinitions()
        =>
        [
            // PDB: ShinePlayer::so_fullbufferitem_box -> CharacterInventory::ci_FillBufferInventoryItem.
            // 0x1F40 is the output buffer capacity, not ShineMob's object count.
            Site("Inventory fill buffer 8000", "FALSE_POSITIVE_8000_BUFFER", 0x00559A7B,
                "68401F00008D55FC528B550C508B4508525081C1D87F0000C745FC00000000E8A1A20E00",
                "PDB: ShinePlayer::so_fullbufferitem_box übergibt 8000 Byte an CharacterInventory::ci_FillBufferInventoryItem (0x00643D40). Bei Mob=12000 unverändert lassen."),

            // PDB: RaidSystem::ResetRaid. Independent 16-bit protocol/state value.
            Site("Raid reset value 8000", "FALSE_POSITIVE_8000_RAID", 0x0063E11A,
                "B8801E000052668906E8C8EFFFFF85C0750FB8401F000066890632C05E5DC208",
                "PDB: RaidSystem::ResetRaid. 0x1F40 ist hier ein separater 16-Bit Raid-/Statuswert, kein ShineMob-Limit."),

            // Generated/member-offset dispatch tables. The surrounding values increase by 8,
            // proving 0x1F40 is an object member offset between 0x1F38 and 0x1F48.
            Site("Member offset sequence 0x1F40 A", "FALSE_POSITIVE_8000_OFFSET", 0x0067CB60,
                "81C1381F0000E92549D8FF8B4DF081C1401F0000E91749D8FF8B4DF081C1481F0000E9",
                "Fortlaufende Member-Offsets 0x1F38/0x1F40/0x1F48; keine Kapazitätsgrenze."),
            Site("Member offset sequence 0x1F40 B", "FALSE_POSITIVE_8000_OFFSET", 0x00681498,
                "81C1381F0000E9EDFFD7FF8B4DE881C1401F0000E9DFFFD7FF8B4DE881C1481F0000E9",
                "Zweite fortlaufende Member-Offset-Tabelle; 0x1F40 darf nicht mit Mob=8000 gekoppelt werden."),

            // Same issue for decimal 1500 / 0x5DC: member offset surrounded by +/- 8.
            Site("Member offset sequence 0x5DC A", "FALSE_POSITIVE_1500_OFFSET", 0x0067A8B4,
                "81C1D4050000E9D16BD8FF8B4DF081C1DC050000E9C36BD8FF8B4DF081C1E4050000E9",
                "Fortlaufende Member-Offsets 0x5D4/0x5DC/0x5E4; kein ShinePlayer-/ShinePet-Limit."),
            Site("Member offset sequence 0x5DC B", "FALSE_POSITIVE_1500_OFFSET", 0x0067ED24,
                "81C1D4050000E96127D8FF8B4DE881C1DC050000E95327D8FF8B4DE881C1E4050000E9",
                "Zweite Member-Offset-Tabelle mit 0x5DC; bei Player=2000 unverändert lassen."),

            // PDB: MapBlockInformationBox::mbib_Load. This 256 is the independent Map/BlockInfo
            // registry capacity already documented elsewhere, not ShineNPC's pool size.
            Site("MapBlockInformationBox limit 256", "FALSE_POSITIVE_256_MAPBLOCK", 0x0049E986,
                "8B8600500B003D000100001BC9F7D975",
                "PDB: MapBlockInformationBox::mbib_Load vergleicht den separaten BlockInfo-Zähler gegen 256. ShineNPC=512 darf diesen Map-Limitwert nicht verändern.")
        ];

    private static FalsePositiveSite Site(string name, string category, uint va, string expectedHex, string detail)
        => new(name, category, va, Convert.FromHexString(expectedHex), detail);

    private static ZonePoolFalsePositiveCheck ToCheck(FalsePositiveSite site, int offset, byte[] actual, bool matches, string? actualText = null)
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

    private static ZonePoolFalsePositiveAuditResult Failed(string detail, string hash = "")
        => new()
        {
            HashMatches = false,
            SitesVerified = false,
            BinarySha256 = hash,
            Detail = detail
        };

    private sealed record FalsePositiveSite(string Name, string Category, uint VirtualAddress, byte[] Expected, string Detail);
}

public sealed class ZonePoolFalsePositiveAuditResult
{
    public bool HashMatches { get; init; }
    public bool SitesVerified { get; init; }
    public string BinarySha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ZonePoolFalsePositiveCheck> Sites { get; init; } = Array.Empty<ZonePoolFalsePositiveCheck>();
}

public sealed class ZonePoolFalsePositiveCheck
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
