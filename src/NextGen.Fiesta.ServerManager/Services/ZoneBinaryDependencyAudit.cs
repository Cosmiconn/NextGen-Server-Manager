using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Read-only verification of additional Zone.exe dependencies that are not part of the
/// central 69-site handle-rebase core: backing-array allocation sizes/counts, initialization
/// loop lengths and the Player quest-buffer cardinality. This class never writes the binary.
/// </summary>
public sealed class ZoneBinaryDependencyAudit
{
    public const int KnownDependencySiteCount = 14;

    private const int PlayerStride = 0x2C058;
    private const int NpcStride = 0x256C;
    private const int MobStride = 0x2568;
    private const int InitRecordStride = 12;

    public ZoneBinaryDependencyAuditResult Analyze(string zoneExePath, ZoneHandleRebasePlan targetLayout)
    {
        if (!targetLayout.IsValid)
            return Failed("Ziel-Handlelayout ist ungültig: " + targetLayout.Detail, layoutValid: false);
        if (string.IsNullOrWhiteSpace(zoneExePath) || !File.Exists(zoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.");

        byte[] image;
        try { image = File.ReadAllBytes(zoneExePath); }
        catch (Exception ex) { return Failed("Zone.exe konnte nicht gelesen werden: " + ex.Message); }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (!hash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Zone.exe-Hash weicht vom verifizierten NA2016-Build ab.", hash);

        IReadOnlyList<DependencySite> definitions;
        try { definitions = BuildDefinitions(targetLayout); }
        catch (Exception ex) { return Failed("Dependency-Matrix konnte nicht aufgebaut werden: " + ex.Message, hash, true); }

        var checks = new List<ZoneBinaryDependencySiteCheck>(definitions.Count);
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

        var countOk = definitions.Count == KnownDependencySiteCount;
        var verified = countOk && checks.All(x => x.Matches);
        var failed = checks.Count(x => !x.Matches) + (countOk ? 0 : 1);
        return new ZoneBinaryDependencyAuditResult
        {
            HashMatches = true,
            LayoutValid = true,
            SitesVerified = verified,
            BinarySha256 = hash,
            Sites = checks,
            Detail = verified
                ? $"Dependency-Audit OK: {checks.Count}/{KnownDependencySiteCount} zusätzliche Player/Mob/NPC-Stellen bytegenau verifiziert."
                : $"Dependency-Audit FEHLGESCHLAGEN: {failed} Abweichung(en), Sites {checks.Count}/{KnownDependencySiteCount}."
        };
    }

    private static IReadOnlyList<DependencySite> BuildDefinitions(ZoneHandleRebasePlan target)
    {
        var stock = ZoneHandleLayout.StockPlan;
        if (!stock.IsValid) throw new InvalidOperationException("Interne Stock-Handlematrix ist ungültig.");

        int S(string name) => stock.Ranges.Single(x => x.Name == name).Capacity;
        int T(string name) => target.Ranges.Single(x => x.Name == name).Capacity;
        var playerStock = S("ShinePlayer");
        var playerTarget = T("ShinePlayer");
        var npcStock = S("ShineNPC");
        var npcTarget = T("ShineNPC");
        var mobStock = S("ShineMob");
        var mobTarget = T("ShineMob");

        return
        [
            // ShineObjectManager::som_Initialize backing array for ShinePlayer.
            Site("Player backing allocation bytes", "SOM_INITIALIZE_ALLOC", 0x0055C5B4, [0x68], AllocationBytes(playerStock, PlayerStride), AllocationBytes(playerTarget, PlayerStride)),
            Site("Player backing element count arg", "SOM_INITIALIZE_COUNT", 0x0055C5D6, [0x68], playerStock, playerTarget),
            Site("Player backing element count header", "SOM_INITIALIZE_COUNT", 0x0055C5E4, [0xC7, 0x00], playerStock, playerTarget),
            Site("Player backing initialization span", "SOM_INITIALIZE_LOOP", 0x0055C6AB, [0x3D], InitializationBytes(playerStock), InitializationBytes(playerTarget)),

            // ShineObjectManager::som_Initialize backing array for ShineNPC.
            Site("NPC backing allocation bytes", "SOM_INITIALIZE_ALLOC", 0x0055C6B6, [0x68], AllocationBytes(npcStock, NpcStride), AllocationBytes(npcTarget, NpcStride)),
            Site("NPC backing element count arg", "SOM_INITIALIZE_COUNT", 0x0055C6D8, [0x68], npcStock, npcTarget),
            Site("NPC backing element count header", "SOM_INITIALIZE_COUNT", 0x0055C6E6, [0xC7, 0x00], npcStock, npcTarget),
            Site("NPC backing initialization span", "SOM_INITIALIZE_LOOP", 0x0055C7AD, [0x3D], InitializationBytes(npcStock), InitializationBytes(npcTarget)),

            // ShineObjectManager::som_Initialize backing array for ShineMob.
            Site("Mob backing allocation bytes", "SOM_INITIALIZE_ALLOC", 0x0055C9BD, [0x68], AllocationBytes(mobStock, MobStride), AllocationBytes(mobTarget, MobStride)),
            Site("Mob backing element count arg", "SOM_INITIALIZE_COUNT", 0x0055C9DF, [0x68], mobStock, mobTarget),
            Site("Mob backing element count header", "SOM_INITIALIZE_COUNT", 0x0055C9ED, [0xC7, 0x00], mobStock, mobTarget),
            Site("Mob backing initialization span", "SOM_INITIALIZE_LOOP", 0x0055CAB4, [0x3D], InitializationBytes(mobStock), InitializationBytes(mobTarget)),

            // Player-specific auxiliary cardinality. The 0x5AF1CC loop emits
            // "Fail to player quest bf alloc" when a slot cannot obtain its quest buffer.
            Site("Player quest-buffer loop", "PLAYER_AUXILIARY", 0x005AF1CC, [0x81, 0xFE], playerStock, playerTarget),

            // Diagnostic only: keeps the startup line "Player Buffer size : %d" truthful.
            Site("Player buffer-size diagnostic", "DIAGNOSTIC", 0x005AE571, [0x68], playerStock, playerTarget)
        ];
    }

    private static int AllocationBytes(int count, int stride)
    {
        var bytes = checked((long)count * stride + 4L);
        if (bytes > int.MaxValue)
            throw new InvalidOperationException($"Backing-Allokation {bytes:N0} Byte überschreitet den freigegebenen 32-Bit-Sicherheitsbereich.");
        return (int)bytes;
    }

    private static int InitializationBytes(int count)
        => checked(count * InitRecordStride);

    private static DependencySite Site(string name, string category, uint va, byte[] prefix, int stock, int target)
        => new(name, category, va, Encode(prefix, stock), Encode(prefix, target));

    private static byte[] Encode(byte[] prefix, int value)
    {
        var bytes = new byte[prefix.Length + 4];
        prefix.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(prefix.Length), value);
        return bytes;
    }

    private static ZoneBinaryDependencySiteCheck ToCheck(DependencySite site, int offset, byte[] actual, bool matches, string? actualText = null)
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

    private static ZoneBinaryDependencyAuditResult Failed(string detail, string hash = "", bool hashMatches = false, bool layoutValid = true)
        => new()
        {
            HashMatches = hashMatches,
            LayoutValid = layoutValid,
            SitesVerified = false,
            BinarySha256 = hash,
            Detail = detail
        };

    private sealed record DependencySite(string Name, string Category, uint VirtualAddress, byte[] Expected, byte[] Replacement);
}

public sealed class ZoneBinaryDependencyAuditResult
{
    public bool HashMatches { get; init; }
    public bool LayoutValid { get; init; }
    public bool SitesVerified { get; init; }
    public string BinarySha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ZoneBinaryDependencySiteCheck> Sites { get; init; } = Array.Empty<ZoneBinaryDependencySiteCheck>();
}

public sealed class ZoneBinaryDependencySiteCheck
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
