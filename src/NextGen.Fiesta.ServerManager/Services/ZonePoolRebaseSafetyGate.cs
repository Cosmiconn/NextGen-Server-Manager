namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Aggregates every read-only proof required to certify the analyzed Player/Mob/NPC
/// rebase surface. Coverage certification and permission to mutate Zone.exe are kept
/// deliberately separate: this gate never writes anything.
/// </summary>
public sealed class ZonePoolRebaseSafetyGate
{
    private readonly ZoneBinaryPatchPreflight _core = new();
    private readonly ZoneBinaryDependencyAudit _dependencies = new();
    private readonly ZonePoolControlFlowAudit _controlFlow = new();
    private readonly ZonePoolFalsePositiveAudit _falsePositives = new();
    private readonly ZoneAllocatorCallInventoryAudit _allocatorInventory = new();
    private readonly ZoneAllocatorDynamicPathAudit _dynamicAllocatorPaths = new();
    private readonly ZonePoolConstantCoverageAudit _constantCoverage = new();
    private readonly ZonePoolConstantSemanticAudit _constantSemantics = new();
    private readonly ZoneNpcPoolCoverageAudit _npcCoverage = new();
    private readonly ZonePoolPatchManifest _patchManifest = new();

    public ZonePoolRebaseSafetyGateResult Evaluate(
        string zoneExePath,
        int playerCapacity,
        int mobCapacity,
        int npcCapacity)
    {
        var layout = ZoneHandleLayout.Plan(playerCapacity, mobCapacity, npcCapacity);
        if (!layout.IsValid)
        {
            return new ZonePoolRebaseSafetyGateResult
            {
                Layout = layout,
                Detail = "BLOCKIERT: " + layout.Detail
            };
        }

        var core = _core.Analyze(zoneExePath, layout);
        var dependencies = _dependencies.Analyze(zoneExePath, layout);
        var controlFlow = _controlFlow.Analyze(zoneExePath);
        var falsePositives = _falsePositives.Analyze(zoneExePath);
        var inventory = _allocatorInventory.Analyze(zoneExePath);
        var dynamicPaths = _dynamicAllocatorPaths.Analyze(zoneExePath);
        var constantCoverage = _constantCoverage.Analyze(zoneExePath);
        var constantSemantics = _constantSemantics.Analyze(zoneExePath, playerCapacity, mobCapacity, npcCapacity);
        var npcCoverage = _npcCoverage.Analyze(zoneExePath, npcCapacity);
        var manifest = _patchManifest.Build(zoneExePath, playerCapacity, mobCapacity, npcCapacity);

        var baselineProofsOk = core.HashMatches
                               && core.LayoutValid
                               && core.CoreSitesVerified
                               && dependencies.HashMatches
                               && dependencies.LayoutValid
                               && dependencies.SitesVerified
                               && controlFlow.HashMatches
                               && controlFlow.EvidenceVerified
                               && falsePositives.HashMatches
                               && falsePositives.SitesVerified
                               && inventory.HashMatches
                               && inventory.InventoryMatches
                               && dynamicPaths.HashMatches
                               && dynamicPaths.EvidenceVerified
                               && dynamicPaths.NoStaticAbsoluteAllocatorPointers;

        var fullCoverageCertified = baselineProofsOk
                                    && constantCoverage.HashMatches
                                    && constantCoverage.InventoryVerified
                                    && constantSemantics.SemanticCoverageVerified
                                    && npcCoverage.CoverageVerified
                                    && manifest.ManifestVerified
                                    && manifest.NoOverlaps
                                    && manifest.RollbackVerified;

        // Coverage certification means that the analyzed binary surface and the exact
        // offline byte manifest are internally complete for the verified NA2016 build.
        // It does NOT authorize mutation. A separate atomic offline writer must still
        // prove backup creation, target-hash verification, stop-state enforcement and
        // rollback before this can ever become true.
        const bool offlineWriterCertified = false;
        var canWrite = fullCoverageCertified && offlineWriterCertified;

        var status = !baselineProofsOk
            ? "Mindestens ein hash-/bytegebundener Basisbeweis ist fehlgeschlagen. Kein Binärschreibpfad zulässig."
            : fullCoverageCertified
                ? "Player/Mob/NPC-Rebase-Coverage ist für den verifizierten NA2016-Zone-Build vollständig zertifiziert: alle rebase-sensitiven Konstanten sind inventarisiert, sämtliche 0x1F40/0x05DC-Codevorkommen semantisch als PATCH oder bewusst unverändert klassifiziert, NPC-Strukturpfade sind geschlossen und das 83-Site-Offline-Manifest inklusive bytegenauem Rollback ist grün. Binärschreiben bleibt gesperrt, bis der atomare Offline-Writer separat zertifiziert ist."
                : "Basisbeweise sind grün, aber die vollständige Rebase-Coverage ist noch nicht zertifiziert. Kein Binärschreibpfad zulässig.";

        return new ZonePoolRebaseSafetyGateResult
        {
            Layout = layout,
            Core = core,
            Dependencies = dependencies,
            ControlFlow = controlFlow,
            FalsePositives = falsePositives,
            AllocatorInventory = inventory,
            DynamicAllocatorPaths = dynamicPaths,
            ConstantCoverage = constantCoverage,
            ConstantSemantics = constantSemantics,
            NpcCoverage = npcCoverage,
            PatchManifest = manifest,
            BaselineProofsVerified = baselineProofsOk,
            FullCoverageCertified = fullCoverageCertified,
            OfflineWriterCertified = offlineWriterCertified,
            CanWriteBinary = canWrite,
            Detail = status
        };
    }
}

public sealed class ZonePoolRebaseSafetyGateResult
{
    public ZoneHandleRebasePlan Layout { get; init; } = new();
    public ZoneBinaryPatchPreflightResult? Core { get; init; }
    public ZoneBinaryDependencyAuditResult? Dependencies { get; init; }
    public ZonePoolControlFlowAuditResult? ControlFlow { get; init; }
    public ZonePoolFalsePositiveAuditResult? FalsePositives { get; init; }
    public ZoneAllocatorCallInventoryAuditResult? AllocatorInventory { get; init; }
    public ZoneAllocatorDynamicPathAuditResult? DynamicAllocatorPaths { get; init; }
    public ZonePoolConstantCoverageAuditResult? ConstantCoverage { get; init; }
    public ZonePoolConstantSemanticAuditResult? ConstantSemantics { get; init; }
    public ZoneNpcPoolCoverageAuditResult? NpcCoverage { get; init; }
    public ZonePoolPatchManifestResult? PatchManifest { get; init; }
    public bool BaselineProofsVerified { get; init; }
    public bool FullCoverageCertified { get; init; }
    public bool OfflineWriterCertified { get; init; }
    public bool CanWriteBinary { get; init; }
    public string Detail { get; init; } = string.Empty;
}
