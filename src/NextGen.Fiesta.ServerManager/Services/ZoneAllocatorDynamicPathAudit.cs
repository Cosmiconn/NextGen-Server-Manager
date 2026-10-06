using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Hash-bound proof for the remaining dynamic direct som_AllocObject callers and for
/// the absence of a statically stored absolute pointer to som_AllocObject. Read-only.
/// </summary>
public sealed class ZoneAllocatorDynamicPathAudit
{
    private const uint SomAllocObjectVa = 0x0054FE20;
    private const int KnownEvidenceSiteCount = 4;

    public ZoneAllocatorDynamicPathAuditResult Analyze(string zoneExePath)
    {
        if (string.IsNullOrWhiteSpace(zoneExePath) || !File.Exists(zoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.");

        byte[] image;
        try { image = File.ReadAllBytes(zoneExePath); }
        catch (Exception ex) { return Failed("Zone.exe konnte nicht gelesen werden: " + ex.Message); }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (!hash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Zone.exe-Hash weicht vom verifizierten NA2016-Build ab.", hash);

        var evidence = new List<ZoneAllocatorDynamicPathEvidence>(KnownEvidenceSiteCount)
        {
            Check(image, 0x004F9232,
                "C68503FDFFFF05",
                "ShineMob/Bandit duplicate default",
                "Typbyte wird vor dem Allocator explizit auf 5 = ShineMob gesetzt."),
            Check(image, 0x004F934E,
                "C68503FDFFFF08",
                "ShineMob/Bandit duplicate alternate",
                "Einziger Alternativpfad setzt dasselbe Typbyte auf 8 = ShineBandit."),
            Check(image, 0x0050DDFF,
                "8B168B82D00400008BCEC745FC00000000FFD00FB6D0528D45EC50B9B8262813E8FC1F0400",
                "ShineMob scene duplicate dynamic type",
                "Der Pfad ruft den virtuellen Objekttyp-Slot +0x4D0 auf, übernimmt AL als Typcode und reicht ihn direkt an som_AllocObject weiter."),
            CheckAscii(image, 0x006D24E4,
                "ShineObjectClass::ShineMob::so_scene_Duplicate",
                "ShineMob duplicate identity",
                "Die im Binärfile eingebettete Funktionskennung bindet den 0x50DDA0/0x50DE1F-Pfad an ShineMob::so_scene_Duplicate.")
        };

        var absolutePattern = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(absolutePattern, SomAllocObjectVa);
        var absolutePointerOffsets = FindOccurrences(image, absolutePattern);
        var evidenceOk = evidence.Count == KnownEvidenceSiteCount && evidence.All(x => x.Matches);
        var noStaticAbsolutePointers = absolutePointerOffsets.Count == 0;

        return new ZoneAllocatorDynamicPathAuditResult
        {
            HashMatches = true,
            EvidenceVerified = evidenceOk,
            NoStaticAbsoluteAllocatorPointers = noStaticAbsolutePointers,
            StaticAbsoluteAllocatorPointerCount = absolutePointerOffsets.Count,
            BinarySha256 = hash,
            Evidence = evidence,
            AbsolutePointerFileOffsets = absolutePointerOffsets,
            Detail = evidenceOk && noStaticAbsolutePointers
                ? "Dynamic-Allocator-Audit OK: 0x4F938F ist ausschließlich ShineMob/ShineBandit (5/8), 0x50DE1F ist ShineMob::so_scene_Duplicate mit virtuellem Objekttyp, und Zone.exe enthält 0 statische absolute Pointer auf som_AllocObject."
                : $"Dynamic-Allocator-Audit FEHLGESCHLAGEN: Evidence {evidence.Count(x => x.Matches)}/{KnownEvidenceSiteCount}, absolute Allocator-Pointer {absolutePointerOffsets.Count}."
        };
    }

    private static ZoneAllocatorDynamicPathEvidence Check(byte[] image, uint va, string expectedHex, string name, string detail)
    {
        var expected = Convert.FromHexString(expectedHex);
        if (!TryVirtualAddressToFileOffset(image, va, out var offset))
            return Evidence(name, detail, va, -1, expectedHex, "VA NICHT ABBILDBAR", false);
        var available = Math.Max(0, Math.Min(expected.Length, image.Length - offset));
        var actual = image.AsSpan(offset, available).ToArray();
        return Evidence(name, detail, va, offset, expectedHex, Convert.ToHexString(actual),
            available == expected.Length && actual.AsSpan().SequenceEqual(expected));
    }

    private static ZoneAllocatorDynamicPathEvidence CheckAscii(byte[] image, uint va, string expectedText, string name, string detail)
    {
        var expected = Encoding.ASCII.GetBytes(expectedText + "\0");
        if (!TryVirtualAddressToFileOffset(image, va, out var offset))
            return Evidence(name, detail, va, -1, Convert.ToHexString(expected), "VA NICHT ABBILDBAR", false);
        var available = Math.Max(0, Math.Min(expected.Length, image.Length - offset));
        var actual = image.AsSpan(offset, available).ToArray();
        return Evidence(name, detail, va, offset, Convert.ToHexString(expected), Convert.ToHexString(actual),
            available == expected.Length && actual.AsSpan().SequenceEqual(expected));
    }

    private static ZoneAllocatorDynamicPathEvidence Evidence(string name, string detail, uint va, int offset, string expected, string actual, bool matches)
        => new()
        {
            Name = name,
            Detail = detail,
            VirtualAddress = va,
            FileOffset = offset,
            ExpectedHex = expected,
            ActualHex = actual,
            Matches = matches
        };

    private static IReadOnlyList<int> FindOccurrences(byte[] image, byte[] pattern)
    {
        var hits = new List<int>();
        for (var i = 0; i <= image.Length - pattern.Length; i++)
        {
            if (image.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                hits.Add(i);
        }
        return hits;
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
                var candidate = checked((long)rawOffset + delta);
                if (candidate < 0 || candidate >= image.Length) return false;
                fileOffset = (int)candidate;
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static ZoneAllocatorDynamicPathAuditResult Failed(string detail, string hash = "")
        => new()
        {
            HashMatches = !string.IsNullOrEmpty(hash),
            EvidenceVerified = false,
            NoStaticAbsoluteAllocatorPointers = false,
            BinarySha256 = hash,
            Detail = detail
        };
}

public sealed class ZoneAllocatorDynamicPathAuditResult
{
    public bool HashMatches { get; init; }
    public bool EvidenceVerified { get; init; }
    public bool NoStaticAbsoluteAllocatorPointers { get; init; }
    public int StaticAbsoluteAllocatorPointerCount { get; init; }
    public string BinarySha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ZoneAllocatorDynamicPathEvidence> Evidence { get; init; } = Array.Empty<ZoneAllocatorDynamicPathEvidence>();
    public IReadOnlyList<int> AbsolutePointerFileOffsets { get; init; } = Array.Empty<int>();
}

public sealed class ZoneAllocatorDynamicPathEvidence
{
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public uint VirtualAddress { get; init; }
    public int FileOffset { get; init; }
    public string ExpectedHex { get; init; } = string.Empty;
    public string ActualHex { get; init; } = string.Empty;
    public bool Matches { get; init; }
}
