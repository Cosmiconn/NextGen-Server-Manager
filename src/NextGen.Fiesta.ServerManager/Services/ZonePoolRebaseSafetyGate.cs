namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Aggregates every read-only proof required to certify the analyzed Player/Mob/NPC
/// rebase surface. Coverage certification, guarded offline-copy generation and live /
/// in-place mutation are deliberately separate capabilities.
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

        var surfaceCoverageCertified = baselineProofsOk
                                       && constantCoverage.HashMatches
                                       && constantCoverage.InventoryVerified
                                       && constantSemantics.SemanticCoverageVerified
                                       && npcCoverage.CoverageVerified
                                       && manifest.ManifestVerified
                                       && manifest.NoOverlaps
                                       && manifest.RollbackVerified;

        // The first write-capable profile is intentionally narrower than the generic
        // rebase planner. It is the exact profile whose resulting image hash is pinned
        // independently by ZonePoolOfflineWriterSelfTest. Other mathematically valid
        // layouts remain analysis-only until they receive their own certified target hash.
        var certifiedOfflineProfile = playerCapacity == ZonePoolOfflineWriterSelfTest.PlayerTarget
                                      && mobCapacity == ZonePoolOfflineWriterSelfTest.MobTarget
                                      && npcCapacity == ZonePoolOfflineWriterSelfTest.NpcTarget
                                      && manifest.ProspectivePatchedSha256.Equals(
                                          ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256,
                                          StringComparison.OrdinalIgnoreCase);

        var fullCoverageCertified = surfaceCoverageCertified && certifiedOfflineProfile;

        // Guarded offline COPY generation is now a distinct capability. The writer is
        // hash-bound, stop-state guarded, refuses in-place writes/overwrites, stages all
        // artifacts, verifies the prospective target hash and emits a baseline backup +
        // manifest. Live process mutation and in-place Zone.exe patching remain forbidden.
        var offlineWriterCertified = fullCoverageCertified;
        var canCreateOfflinePatchedCopy = fullCoverageCertified && offlineWriterCertified;
        const bool canWriteLiveOrInPlaceBinary = false;

        var status = !baselineProofsOk
            ? "Mindestens ein hash-/bytegebundener Basisbeweis ist fehlgeschlagen. Kein Binärschreibpfad zulässig."
            : !surfaceCoverageCertified
                ? "Basisbeweise sind grün, aber die vollständige Rebase-Coverage ist noch nicht zertifiziert. Kein Binärschreibpfad zulässig."
                : !certifiedOfflineProfile
                    ? "Rebase-Surface ist vollständig analysiert, aber dieses Zielprofil besitzt noch keinen unabhängig gepinnten Patch-SHA. Nur Analyse zulässig."
                    : "Player/Mob/NPC-Rebase-Coverage und der guarded Offline-COPY-Writer sind für das zertifizierte Profil 2000/12000/512 freigegeben. Die Original-Zone.exe wird niemals in-place verändert; laufende Zone-Prozesse blockieren den Writer. Live-/In-Place-Patching bleibt hart gesperrt.";

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
            SurfaceCoverageCertified = surfaceCoverageCertified,
            CertifiedOfflineProfile = certifiedOfflineProfile,
            FullCoverageCertified = fullCoverageCertified,
            OfflineWriterCertified = offlineWriterCertified,
            CanCreateOfflinePatchedCopy = canCreateOfflinePatchedCopy,
            CanWriteBinary = canWriteLiveOrInPlaceBinary,
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
    public bool SurfaceCoverageCertified { get; init; }
    public bool CertifiedOfflineProfile { get; init; }
    public bool FullCoverageCertified { get; init; }
    public bool OfflineWriterCertified { get; init; }
    public bool CanCreateOfflinePatchedCopy { get; init; }
    /// <summary>
    /// Always false: live process writes and in-place Zone.exe mutation are not authorized.
    /// </summary>
    public bool CanWriteBinary { get; init; }
    public string Detail { get; init; } = string.Empty;
}
