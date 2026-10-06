namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Semantic partition for the two ambiguous stock constants that cannot be treated as
/// global replacements: 0x1F40 is both Mob capacity and the stock Player handle base;
/// 0x05DC is both Player capacity and the unchanged Pet capacity. Every executable-code
/// occurrence known to the baseline inventory must be classified exactly once as PATCH
/// or an intentionally immutable domain use.
/// </summary>
public sealed class ZonePoolConstantSemanticAudit
{
    private static readonly uint[] Patch1F40 =
    [
        0x00548E08, 0x00548E4A, 0x00556A52, 0x005576D1,
        0x00559D72, 0x0055C9E0, 0x0055C9EF, 0x00633657
    ];

    private static readonly uint[] MemberOffset1F40 =
    [
        0x00402FA8, 0x0041126D, 0x004153DB, 0x004153E4,
        0x0055F1C8, 0x0055F2CF, 0x0055FEF3, 0x0056B1F2,
        0x0067CB70, 0x006814A8
    ];

    private static readonly uint[] FixedBufferOrBound1F40 =
    [
        0x0040DFAD, 0x00417D63, 0x004392D3, 0x004392E1, 0x004392E8,
        0x0044D82A, 0x0044E009, 0x0044E090, 0x0045406A, 0x004541E8,
        0x00454861, 0x00454D4B, 0x004551F6, 0x004560D1, 0x004561A1,
        0x00456351, 0x00456431, 0x004CBC6F, 0x004CBDBC, 0x0050E68F,
        0x0050E86F, 0x00559A7C, 0x005BB931, 0x005BBB78, 0x005BBCA2,
        0x005BC812
    ];

    private static readonly uint[] OtherDomain1F40 = [0x0063E12D];

    private static readonly uint[] Patch05DC =
    [
        0x00548E37, 0x00557623, 0x00559D51, 0x0055C5D7,
        0x0055C5E6, 0x005AE572, 0x005AF1CE
    ];

    private static readonly uint[] PetPool05DC =
    [
        0x00549047, 0x00557321, 0x0055782D, 0x0055CEEC, 0x0055CEFB
    ];

    private static readonly uint[] MemberOffset05DC =
    [
        0x0040204F, 0x00404E0E, 0x0040F659, 0x00412415, 0x0041241E,
        0x0048C9B0, 0x0048CE05, 0x00565186, 0x005CBFA5,
        0x0067A8C4, 0x0067ED34
    ];

    private readonly ZonePoolConstantCoverageAudit _inventory = new();
    private readonly ZonePoolPatchManifest _manifest = new();

    public ZonePoolConstantSemanticAuditResult Analyze(
        string zoneExePath,
        int playerCapacity,
        int mobCapacity,
        int npcCapacity)
    {
        var inventory = _inventory.Analyze(zoneExePath);
        var manifest = _manifest.Build(zoneExePath, playerCapacity, mobCapacity, npcCapacity);
        if (!inventory.HashMatches || !inventory.InventoryVerified)
            return Failed("Rohinventur der rebase-sensitiven Konstanten ist nicht grün.", inventory, manifest);
        if (!manifest.ManifestVerified || !manifest.NoOverlaps || !manifest.RollbackVerified)
            return Failed("83-Site-Patchmanifest ist nicht grün.", inventory, manifest);

        var c1 = inventory.Checks.SingleOrDefault(x => x.Value == 0x1F40);
        var c5 = inventory.Checks.SingleOrDefault(x => x.Value == 0x05DC);
        if (c1 is null || c5 is null)
            return Failed("0x1F40- oder 0x05DC-Inventur fehlt.", inventory, manifest);

        var class1 = BuildClassification(
            ("PATCH", Patch1F40),
            ("MEMBER_OFFSET", MemberOffset1F40),
            ("FIXED_BUFFER_OR_BOUND", FixedBufferOrBound1F40),
            ("OTHER_DOMAIN", OtherDomain1F40));
        var class5 = BuildClassification(
            ("PATCH", Patch05DC),
            ("PET_POOL_UNCHANGED", PetPool05DC),
            ("MEMBER_OFFSET", MemberOffset05DC));

        var partition1 = PartitionMatches(c1.ActualVirtualAddresses, class1);
        var partition5 = PartitionMatches(c5.ActualVirtualAddresses, class5);

        // Every address marked PATCH must really be the 4-byte stock value contained in
        // an exact manifest site's ExpectedBytes. This prevents the semantic table from
        // accidentally blessing a would-be patch address that the writer never changes.
        var manifestPatchRawAddresses = new HashSet<uint>();
        foreach (var site in manifest.ChangedSites)
        {
            AddRawValueAddress(site, 0x1F40, manifestPatchRawAddresses);
            AddRawValueAddress(site, 0x05DC, manifestPatchRawAddresses);
        }
        var patch1Manifest = Patch1F40.All(manifestPatchRawAddresses.Contains);
        var patch5Manifest = Patch05DC.All(manifestPatchRawAddresses.Contains);

        var verified = partition1 && partition5 && patch1Manifest && patch5Manifest;
        return new ZonePoolConstantSemanticAuditResult
        {
            Inventory = inventory,
            Manifest = manifest,
            Classification1F40 = class1,
            Classification05DC = class5,
            EveryOccurrenceClassifiedExactlyOnce = partition1 && partition5,
            PatchOccurrencesBackedByManifest = patch1Manifest && patch5Manifest,
            SemanticCoverageVerified = verified,
            Detail = verified
                ? $"Semantik-Audit OK: 0x1F40 {c1.ActualVirtualAddresses.Count}/{c1.ActualVirtualAddresses.Count} und 0x05DC {c5.ActualVirtualAddresses.Count}/{c5.ActualVirtualAddresses.Count} Vorkommen exakt einmal klassifiziert. PATCH-Adressen sind vollständig durch das 83-Site-Manifest gedeckt; Pet-Limits, Member-Offets und 8-KB-Domainwerte bleiben bewusst unverändert."
                : "Semantik-Audit FEHLGESCHLAGEN: mindestens ein 0x1F40/0x05DC-Vorkommen ist unklassifiziert, doppelt klassifiziert oder nicht durch das Patchmanifest gedeckt."
        };
    }

    private static IReadOnlyList<ZonePoolConstantSemanticEntry> BuildClassification(params (string Category, uint[] Addresses)[] groups)
        => groups.SelectMany(group => group.Addresses.Select(address => new ZonePoolConstantSemanticEntry
        {
            VirtualAddress = address,
            Category = group.Category
        })).OrderBy(x => x.VirtualAddress).ToArray();

    private static bool PartitionMatches(IReadOnlyList<uint> actual, IReadOnlyList<ZonePoolConstantSemanticEntry> classification)
    {
        var actualOrdered = actual.OrderBy(x => x).ToArray();
        var classifiedOrdered = classification.Select(x => x.VirtualAddress).OrderBy(x => x).ToArray();
        return classifiedOrdered.Distinct().Count() == classifiedOrdered.Length
               && actualOrdered.SequenceEqual(classifiedOrdered);
    }

    private static void AddRawValueAddress(ZonePoolPatchManifestSite site, uint value, HashSet<uint> result)
    {
        var pattern = BitConverter.GetBytes(value);
        for (var i = 0; i <= site.ExpectedBytes.Length - pattern.Length; i++)
        {
            if (site.ExpectedBytes.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                result.Add(site.VirtualAddress + (uint)i);
        }
    }

    private static ZonePoolConstantSemanticAuditResult Failed(
        string detail,
        ZonePoolConstantCoverageAuditResult? inventory = null,
        ZonePoolPatchManifestResult? manifest = null)
        => new()
        {
            Inventory = inventory,
            Manifest = manifest,
            SemanticCoverageVerified = false,
            Detail = detail
        };
}

public sealed class ZonePoolConstantSemanticAuditResult
{
    public ZonePoolConstantCoverageAuditResult? Inventory { get; init; }
    public ZonePoolPatchManifestResult? Manifest { get; init; }
    public IReadOnlyList<ZonePoolConstantSemanticEntry> Classification1F40 { get; init; } = Array.Empty<ZonePoolConstantSemanticEntry>();
    public IReadOnlyList<ZonePoolConstantSemanticEntry> Classification05DC { get; init; } = Array.Empty<ZonePoolConstantSemanticEntry>();
    public bool EveryOccurrenceClassifiedExactlyOnce { get; init; }
    public bool PatchOccurrencesBackedByManifest { get; init; }
    public bool SemanticCoverageVerified { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed class ZonePoolConstantSemanticEntry
{
    public uint VirtualAddress { get; init; }
    public string Category { get; init; } = string.Empty;
}
