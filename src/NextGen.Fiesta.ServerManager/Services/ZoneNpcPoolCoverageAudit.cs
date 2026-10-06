namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Structural proof for ShineNPC capacity. A raw scan for decimal 256 is intentionally
/// rejected because Zone.exe contains hundreds of unrelated 0x100 constants. Instead
/// this gate requires every verified NPC-specific pool/list/handle site plus the complete
/// direct allocator inventory and dynamic-path proof.
/// </summary>
public sealed class ZoneNpcPoolCoverageAudit
{
    private static readonly uint[] RequiredNpcCoreVas =
    [
        0x0055765C, // ShineObjectManager ctor max = 256
        0x00548F26, // generic NPC handle limit = 256
        0x00548F39, // generic NPC handle base = 0x42BC
        0x00557C80, // class-specific NPC handle limit = 256
        0x00557CA1, // class-specific NPC handle base = 0x42BC
        0x006336DB, // preceding Axial end / NPC start boundary
        0x006336F6, // NPC start
        0x00633700, // NPC end / Bandit start
        0x0063370D  // NPC local-index subtraction
    ];

    private static readonly uint[] RequiredNpcDependencyVas =
    [
        0x0055C6B6, // backing allocation bytes
        0x0055C6D8, // backing element-count argument
        0x0055C6E6, // backing element-count header
        0x0055C7AD  // backing initialization span
    ];

    private static readonly uint[] RequiredDirectNpcAllocatorCalls =
    [
        0x004C6650,
        0x004C6EB7,
        0x004E3B4A,
        0x004E3F0A,
        0x004ED597,
        0x004F9654
    ];

    private readonly ZoneBinaryPatchPreflight _core = new();
    private readonly ZoneBinaryDependencyAudit _dependencies = new();
    private readonly ZonePoolControlFlowAudit _controlFlow = new();
    private readonly ZoneAllocatorCallInventoryAudit _inventory = new();
    private readonly ZoneAllocatorDynamicPathAudit _dynamicPaths = new();

    public ZoneNpcPoolCoverageAuditResult Analyze(string zoneExePath, int targetNpcCapacity)
    {
        var target = ZoneHandleLayout.Plan(ZoneHandleLayout.StockPlayer, ZoneHandleLayout.StockMob, targetNpcCapacity);
        if (!target.IsValid)
            return Failed("NPC-Ziellayout ungültig: " + target.Detail, target);

        var core = _core.Analyze(zoneExePath, target);
        var dependencies = _dependencies.Analyze(zoneExePath, target);
        var flow = _controlFlow.Analyze(zoneExePath);
        var inventory = _inventory.Analyze(zoneExePath);
        var dynamicPaths = _dynamicPaths.Analyze(zoneExePath);

        var coreByVa = core.Sites.ToDictionary(x => x.VirtualAddress);
        var dependencyByVa = dependencies.Sites.ToDictionary(x => x.VirtualAddress);
        var npcCoreComplete = RequiredNpcCoreVas.All(va => coreByVa.TryGetValue(va, out var site) && site.Matches);
        var npcDependenciesComplete = RequiredNpcDependencyVas.All(va => dependencyByVa.TryGetValue(va, out var site) && site.Matches);
        var directNpcCallsComplete = inventory.InventoryMatches
                                     && inventory.DirectNpcType4CallCount == RequiredDirectNpcAllocatorCalls.Length
                                     && RequiredDirectNpcAllocatorCalls.All(va => inventory.ActualCallSites.Contains(va));

        var centralNpcFlowEvidence = flow.EvidenceVerified
                                     && flow.Sites.Count(x => x.Category == "CENTRAL_ALLOCATOR_NPC" && x.Matches) >= 3;
        var dynamicPathsExcludeHiddenNpc = dynamicPaths.EvidenceVerified
                                           && dynamicPaths.NoStaticAbsoluteAllocatorPointers;

        var verified = core.HashMatches
                       && core.LayoutValid
                       && core.CoreSitesVerified
                       && dependencies.HashMatches
                       && dependencies.LayoutValid
                       && dependencies.SitesVerified
                       && npcCoreComplete
                       && npcDependenciesComplete
                       && directNpcCallsComplete
                       && centralNpcFlowEvidence
                       && dynamicPathsExcludeHiddenNpc;

        return new ZoneNpcPoolCoverageAuditResult
        {
            TargetLayout = target,
            Core = core,
            Dependencies = dependencies,
            ControlFlow = flow,
            AllocatorInventory = inventory,
            DynamicAllocatorPaths = dynamicPaths,
            NpcCoreSitesVerified = npcCoreComplete,
            NpcBackingSitesVerified = npcDependenciesComplete,
            DirectNpcAllocatorCallsVerified = directNpcCallsComplete,
            CentralNpcControlFlowVerified = centralNpcFlowEvidence,
            DynamicPathsExcludeHiddenNpc = dynamicPathsExcludeHiddenNpc,
            CoverageVerified = verified,
            Detail = verified
                ? $"NPC-Strukturcoverage OK: {RequiredNpcCoreVas.Length} Core-Sites, {RequiredNpcDependencyVas.Length} Backing-Sites, exakt {RequiredDirectNpcAllocatorCalls.Length} direkte Type-4-Allocatorpfade und zentrale NPC-Control-Flow-Belege sind hash-/bytegebunden verifiziert. Rohwertscan nach 0x100 ist nicht erforderlich."
                : "NPC-Strukturcoverage FEHLGESCHLAGEN: mindestens ein NPC-spezifischer Pool-, Backing-, Handle- oder Allocatorbeweis fehlt bzw. weicht vom verifizierten Build ab."
        };
    }

    private static ZoneNpcPoolCoverageAuditResult Failed(string detail, ZoneHandleRebasePlan layout)
        => new()
        {
            TargetLayout = layout,
            CoverageVerified = false,
            Detail = detail
        };
}

public sealed class ZoneNpcPoolCoverageAuditResult
{
    public ZoneHandleRebasePlan TargetLayout { get; init; } = new();
    public ZoneBinaryPatchPreflightResult? Core { get; init; }
    public ZoneBinaryDependencyAuditResult? Dependencies { get; init; }
    public ZonePoolControlFlowAuditResult? ControlFlow { get; init; }
    public ZoneAllocatorCallInventoryAuditResult? AllocatorInventory { get; init; }
    public ZoneAllocatorDynamicPathAuditResult? DynamicAllocatorPaths { get; init; }
    public bool NpcCoreSitesVerified { get; init; }
    public bool NpcBackingSitesVerified { get; init; }
    public bool DirectNpcAllocatorCallsVerified { get; init; }
    public bool CentralNpcControlFlowVerified { get; init; }
    public bool DynamicPathsExcludeHiddenNpc { get; init; }
    public bool CoverageVerified { get; init; }
    public string Detail { get; init; } = string.Empty;
}
