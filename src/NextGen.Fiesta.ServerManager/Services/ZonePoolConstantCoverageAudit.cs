using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Baseline inventory for rebase-sensitive 32-bit constants in executable Zone.exe
/// sections. This is intentionally an occurrence inventory, not a global replace list:
/// many 0x1F40/0x5DC hits are packet sizes or member offsets and MUST remain unchanged.
/// NPC 0x100 is deliberately excluded because hundreds of unrelated 256 constants make
/// raw-value coverage semantically useless; NPC is proven through structural/call-flow audits.
/// </summary>
public sealed class ZonePoolConstantCoverageAudit
{
    private static readonly IReadOnlyDictionary<uint, uint[]> ExpectedOccurrences =
        new Dictionary<uint, uint[]>
        {
            [0x1F40] =
            [
                0x402FA8,0x40DFAD,0x41126D,0x4153DB,0x4153E4,0x417D63,0x4392D3,0x4392E1,0x4392E8,
                0x44D82A,0x44E009,0x44E090,0x45406A,0x4541E8,0x454861,0x454D4B,0x4551F6,0x4560D1,
                0x4561A1,0x456351,0x456431,0x4CBC6F,0x4CBDBC,0x50E68F,0x50E86F,0x548E08,0x548E4A,
                0x556A52,0x5576D1,0x559A7C,0x559D72,0x55C9E0,0x55C9EF,0x55F1C8,0x55F2CF,0x55FEF3,
                0x56B1F2,0x5BB931,0x5BBB78,0x5BBCA2,0x5BC812,0x633657,0x63E12D,0x67CB70,0x6814A8
            ],
            [0x05DC] =
            [
                0x40204F,0x404E0E,0x40F659,0x412415,0x41241E,0x48C9B0,0x48CE05,0x548E37,0x549047,
                0x557321,0x557623,0x55782D,0x559D51,0x55C5D7,0x55C5E6,0x55CEEC,0x55CEFB,0x565186,
                0x5AE572,0x5AF1CE,0x5CBFA5,0x67A8C4,0x67ED34
            ],
            [0x251C] = [0x402ACF,0x411760,0x548E7A,0x555E92,0x63366D,0x633688,0x67D2C4,0x681C94],
            [0x2904] = [0x412844,0x548EAA,0x5552F2,0x633692,0x6336AD],
            [0x34BC] = [0x548EDA,0x5562F2,0x6336B7,0x6336D2],
            [0x42BC] = [0x548F3A,0x557CA2,0x6336DC,0x6336F7],
            [0x43BC] = [0x548F0A,0x556F02,0x55C0E0,0x633701,0x63371C],
            [0x4BBC] = [0x548F6A,0x555482,0x633726,0x633741],
            [0x4FA4] = [0x548F9A,0x555CD2,0x63374B,0x633766],
            [0x509E] = [0x548FCA,0x555BD2,0x633770,0x63378B],
            [0x5486] = [0x548FFA,0x557072,0x633795,0x6337B0],
            [0x567A] = [0x54902A,0x55EE22,0x6337BA,0x6337D5],
            [0x5A62] = [0x6337DF],
            [0x5C56] = [0x54905A,0x557342,0x6337FA],
            [0x6232] = [0x633804]
        };

    public ZonePoolConstantCoverageAuditResult Analyze(string zoneExePath)
    {
        if (string.IsNullOrWhiteSpace(zoneExePath) || !File.Exists(zoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.");

        byte[] image;
        try { image = File.ReadAllBytes(zoneExePath); }
        catch (Exception ex) { return Failed("Zone.exe konnte nicht gelesen werden: " + ex.Message); }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (!hash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Zone.exe-Hash weicht vom verifizierten NA2016-Build ab.", hash);
        if (!TryReadExecutableSections(image, out var imageBase, out var sections))
            return Failed("PE-Executable-Sections konnten nicht gelesen werden.", hash);

        var checks = new List<ZonePoolConstantCoverageCheck>();
        foreach (var pair in ExpectedOccurrences.OrderBy(x => x.Key))
        {
            var actual = FindDwordOccurrences(image, imageBase, sections, pair.Key).OrderBy(x => x).ToArray();
            var expected = pair.Value.OrderBy(x => x).ToArray();
            checks.Add(new ZonePoolConstantCoverageCheck
            {
                Value = pair.Key,
                ExpectedVirtualAddresses = expected,
                ActualVirtualAddresses = actual,
                Matches = expected.SequenceEqual(actual)
            });
        }

        var verified = checks.All(x => x.Matches);
        return new ZonePoolConstantCoverageAuditResult
        {
            HashMatches = true,
            InventoryVerified = verified,
            BinarySha256 = hash,
            Checks = checks,
            Detail = verified
                ? $"Constant-Inventur OK: {checks.Count} rebase-sensitive 32-Bit-Werte besitzen exakt die erwarteten Vorkommen im ausführbaren Code. 0x1F40={checks.Single(x => x.Value == 0x1F40).ActualVirtualAddresses.Count}, 0x5DC={checks.Single(x => x.Value == 0x5DC).ActualVirtualAddresses.Count}. NPC 0x100 bleibt bewusst strukturell statt per Rohwertscan geprüft."
                : "Constant-Inventur ABWEICHEND: mindestens ein rebase-sensitiver Wert besitzt neue, fehlende oder verschobene Code-Vorkommen."
        };
    }

    private static IReadOnlyList<uint> FindDwordOccurrences(byte[] image, uint imageBase, IReadOnlyList<ExecutableSection> sections, uint value)
    {
        var pattern = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, value);
        var result = new List<uint>();
        foreach (var section in sections)
        {
            var size = Math.Min(section.RawSize, image.Length - section.RawOffset);
            for (var i = 0; i <= size - pattern.Length; i++)
            {
                if (image.AsSpan(section.RawOffset + i, pattern.Length).SequenceEqual(pattern))
                    result.Add(checked(imageBase + section.VirtualAddress + (uint)i));
            }
        }
        return result;
    }

    private static bool TryReadExecutableSections(byte[] image, out uint imageBase, out IReadOnlyList<ExecutableSection> sections)
    {
        imageBase = 0;
        sections = Array.Empty<ExecutableSection>();
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
            imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optionalOffset + 28, 4));
            var list = new List<ExecutableSection>();
            var sectionOffset = optionalOffset + optionalSize;
            for (var i = 0; i < sectionCount; i++)
            {
                var sh = sectionOffset + i * 40;
                if (sh + 40 > image.Length) return false;
                var va = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 12, 4));
                var rawSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 16, 4)));
                var rawOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 20, 4)));
                var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sh + 36, 4));
                if ((characteristics & 0x20000000) == 0) continue;
                if (rawOffset < 0 || rawOffset > image.Length) return false;
                list.Add(new ExecutableSection(va, rawOffset, rawSize));
            }
            sections = list;
            return list.Count > 0;
        }
        catch { return false; }
    }

    private static ZonePoolConstantCoverageAuditResult Failed(string detail, string hash = "") => new()
    {
        HashMatches = !string.IsNullOrEmpty(hash),
        InventoryVerified = false,
        BinarySha256 = hash,
        Detail = detail
    };

    private readonly record struct ExecutableSection(uint VirtualAddress, int RawOffset, int RawSize);
}

public sealed class ZonePoolConstantCoverageAuditResult
{
    public bool HashMatches { get; init; }
    public bool InventoryVerified { get; init; }
    public string BinarySha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ZonePoolConstantCoverageCheck> Checks { get; init; } = Array.Empty<ZonePoolConstantCoverageCheck>();
}

public sealed class ZonePoolConstantCoverageCheck
{
    public uint Value { get; init; }
    public bool Matches { get; init; }
    public IReadOnlyList<uint> ExpectedVirtualAddresses { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> ActualVirtualAddresses { get; init; } = Array.Empty<uint>();
}
