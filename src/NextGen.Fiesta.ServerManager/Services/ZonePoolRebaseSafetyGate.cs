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
                               && inventory.InventoryMatches;

        // This remains deliberately false. Two dynamic direct allocator callers plus any
        // indirect allocator call paths still need final semantic classification before
        // we may certify full Player/Mob/NPC dependency coverage.
        const bool fullCoverageCertified = false;
        var canWrite = baselineProofsOk && fullCoverageCertified;

        var status = baselineProofsOk
            ? "Alle derzeit implementierten Beweise sind grün. Vollabdeckung bleibt gesperrt: zwei dynamische direkte Allocator-Pfade und indirekte Aufrufe sind noch abschließend zu klassifizieren."
            : "Mindestens ein hash-/bytegebundener Sicherheitsbeweis ist fehlgeschlagen. Kein Binärschreibpfad zulässig.";

        return new ZonePoolRebaseSafetyGateResult
        {
            Layout = layout,
            Core = core,
            Dependencies = dependencies,
            ControlFlow = controlFlow,
            FalsePositives = falsePositives,
            AllocatorInventory = inventory,
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
    public bool BaselineProofsVerified { get; init; }
    public bool FullCoverageCertified { get; init; }
    public bool CanWriteBinary { get; init; }
    public string Detail { get; init; } = string.Empty;
}
