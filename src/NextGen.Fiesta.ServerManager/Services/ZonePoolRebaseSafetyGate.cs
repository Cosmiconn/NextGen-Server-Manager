namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Aggregates every current read-only proof required before a Player/Mob/NPC binary
/// rebase can ever be considered. It deliberately cannot write anything.
/// </summary>
public sealed class ZonePoolRebaseSafetyGate
{
    private readonly ZoneBinaryPatchPreflight _core = new();
    private readonly ZoneBinaryDependencyAudit _dependencies = new();
    private readonly ZonePoolControlFlowAudit _controlFlow = new();
    private readonly ZonePoolFalsePositiveAudit _falsePositives = new();
    private readonly ZoneAllocatorCallInventoryAudit _allocatorInventory = new();
    private readonly ZoneAllocatorDynamicPathAudit _dynamicAllocatorPaths = new();

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

        // The allocator-call graph is now closed for all direct calls and contains no
        // statically stored absolute allocator pointer. Full patch coverage is still kept
        // false until the complete set of mutable rebase bytes (core + auxiliary sites)
        // is promoted into one transactional offline patch manifest with rollback proof.
        const bool fullCoverageCertified = false;
        var canWrite = baselineProofsOk && fullCoverageCertified;

        var status = baselineProofsOk
            ? "Alle aktuellen Hash-/Byte-/Control-Flow-/Allocator-Beweise sind grün. Direkte Allocator-Pfade sind vollständig inventarisiert und die dynamischen ShineMob-Pfade klassifiziert. Schreiben bleibt gesperrt, bis Core- und Zusatzabhängigkeiten in einem vollständigen transaktionalen Offline-Patchmanifest mit Rollback-Nachweis zusammengeführt sind."
            : "Mindestens ein hash-/bytegebundener Sicherheitsbeweis ist fehlgeschlagen. Kein Binärschreibpfad zulässig.";

        return new ZonePoolRebaseSafetyGateResult
        {
            Layout = layout,
            Core = core,
            Dependencies = dependencies,
            ControlFlow = controlFlow,
            FalsePositives = falsePositives,
            AllocatorInventory = inventory,
            DynamicAllocatorPaths = dynamicPaths,
            BaselineProofsVerified = baselineProofsOk,
            FullCoverageCertified = fullCoverageCertified,
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
    public bool BaselineProofsVerified { get; init; }
    public bool FullCoverageCertified { get; init; }
    public bool CanWriteBinary { get; init; }
    public string Detail { get; init; } = string.Empty;
}
