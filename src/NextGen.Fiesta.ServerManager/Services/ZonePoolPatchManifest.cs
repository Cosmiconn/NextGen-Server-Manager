using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Builds and verifies a deterministic OFFLINE patch manifest from the already proven
/// core and auxiliary Player/Mob/NPC rebase sites. It only mutates an in-memory copy,
/// computes the prospective patched hash and proves byte-for-byte rollback to baseline.
/// It never writes Zone.exe or process memory.
/// </summary>
public sealed class ZonePoolPatchManifest
{
    private readonly ZoneBinaryPatchPreflight _core = new();
    private readonly ZoneBinaryDependencyAudit _dependencies = new();

    public ZonePoolPatchManifestResult Build(
        string zoneExePath,
        int playerCapacity,
        int mobCapacity,
        int npcCapacity)
    {
        var layout = ZoneHandleLayout.Plan(playerCapacity, mobCapacity, npcCapacity);
        if (!layout.IsValid)
            return Failed("Ziel-Handlelayout ist ungültig: " + layout.Detail, layout);
        if (string.IsNullOrWhiteSpace(zoneExePath) || !File.Exists(zoneExePath))
            return Failed("Zone.exe wurde nicht gefunden.", layout);

        byte[] baseline;
        try { baseline = File.ReadAllBytes(zoneExePath); }
        catch (Exception ex) { return Failed("Zone.exe konnte nicht gelesen werden: " + ex.Message, layout); }

        var baselineHash = Convert.ToHexString(SHA256.HashData(baseline));
        if (!baselineHash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase))
            return Failed("Zone.exe-Hash weicht vom verifizierten NA2016-Build ab.", layout, baselineHash);

        var core = _core.Analyze(zoneExePath, layout);
        var dependencies = _dependencies.Analyze(zoneExePath, layout);
        if (!core.HashMatches || !core.LayoutValid || !core.CoreSitesVerified)
            return Failed("69-Site Core-Preflight ist nicht vollständig grün: " + core.Detail, layout, baselineHash, core, dependencies);
        if (!dependencies.HashMatches || !dependencies.LayoutValid || !dependencies.SitesVerified)
            return Failed("14-Site Dependency-Audit ist nicht vollständig grün: " + dependencies.Detail, layout, baselineHash, core, dependencies);

        var sites = new List<ZonePoolPatchManifestSite>();
        sites.AddRange(core.Sites.Select(x => ConvertSite(
            "CORE", x.Name, x.Category, x.VirtualAddress, x.FileOffset,
            x.ExpectedHex, x.ActualHex, x.ReplacementHex, x.Matches)));
        sites.AddRange(dependencies.Sites.Select(x => ConvertSite(
            "AUX", x.Name, x.Category, x.VirtualAddress, x.FileOffset,
            x.ExpectedHex, x.ActualHex, x.ReplacementHex, x.Matches)));

        if (sites.Count != ZoneBinaryPatchPreflight.KnownCoreSiteCount + ZoneBinaryDependencyAudit.KnownDependencySiteCount)
            return Failed($"Manifest-Sitezahl unerwartet: {sites.Count} statt 83.", layout, baselineHash, core, dependencies, sites);

        if (sites.Any(x => !x.Matches || x.FileOffset < 0 || x.ExpectedBytes.Length == 0 || x.ExpectedBytes.Length != x.ReplacementBytes.Length))
            return Failed("Mindestens eine Manifest-Site ist nicht bytegenau verifiziert oder ändert die Instruktionslänge.", layout, baselineHash, core, dependencies, sites);

        var ordered = sites.OrderBy(x => x.FileOffset).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            var previousEnd = ordered[i - 1].FileOffset + ordered[i - 1].ExpectedBytes.Length;
            if (ordered[i].FileOffset < previousEnd)
            {
                return Failed(
                    $"Patch-Sites überlappen: {ordered[i - 1].Name} und {ordered[i].Name}.",
                    layout, baselineHash, core, dependencies, sites);
            }
        }

        var changed = ordered.Where(x => x.WouldChange).ToArray();
        var simulated = baseline.ToArray();
        foreach (var site in changed)
        {
            if (!simulated.AsSpan(site.FileOffset, site.ExpectedBytes.Length).SequenceEqual(site.ExpectedBytes))
                return Failed("Simulations-Precondition verlorengangen bei " + site.Name + ".", layout, baselineHash, core, dependencies, sites);
            site.ReplacementBytes.CopyTo(simulated, site.FileOffset);
        }

        var patchedHash = Convert.ToHexString(SHA256.HashData(simulated));

        // Transaction rollback proof: reverse every changed site and require the exact
        // original bytes AND the exact baseline SHA-256 again.
        foreach (var site in changed.Reverse())
        {
            if (!simulated.AsSpan(site.FileOffset, site.ReplacementBytes.Length).SequenceEqual(site.ReplacementBytes))
                return Failed("Rollback-Precondition fehlgeschlagen bei " + site.Name + ".", layout, baselineHash, core, dependencies, sites);
            site.ExpectedBytes.CopyTo(simulated, site.FileOffset);
        }

        var rollbackHash = Convert.ToHexString(SHA256.HashData(simulated));
        var rollbackVerified = rollbackHash.Equals(baselineHash, StringComparison.OrdinalIgnoreCase)
                               && simulated.AsSpan().SequenceEqual(baseline);
        if (!rollbackVerified)
            return Failed("Rollback-Simulation kehrte nicht bytegenau zum Baseline-Build zurück.", layout, baselineHash, core, dependencies, sites);

        return new ZonePoolPatchManifestResult
        {
            Layout = layout,
            Core = core,
            Dependencies = dependencies,
            ManifestVerified = true,
            NoOverlaps = true,
            RollbackVerified = true,
            BaselineSha256 = baselineHash,
            ProspectivePatchedSha256 = patchedHash,
            RollbackSha256 = rollbackHash,
            Sites = ordered,
            ChangedSites = changed,
            Detail = $"Offline-Manifest OK: {ordered.Length} verifizierte Sites, {changed.Length} Änderung(en), keine Überlappung; simuliertes Patch-SHA {patchedHash}; Rollback bytegenau auf {baselineHash}. Keine Datei wurde geschrieben."
        };
    }

    private static ZonePoolPatchManifestSite ConvertSite(
        string source, string name, string category, uint va, int offset,
        string expectedHex, string actualHex, string replacementHex, bool matches)
    {
        byte[] expected;
        byte[] replacement;
        try
        {
            expected = Convert.FromHexString(expectedHex);
            replacement = Convert.FromHexString(replacementHex);
        }
        catch
        {
            expected = Array.Empty<byte>();
            replacement = Array.Empty<byte>();
            matches = false;
        }

        return new ZonePoolPatchManifestSite
        {
            Source = source,
            Name = name,
            Category = category,
            VirtualAddress = va,
            FileOffset = offset,
            ExpectedHex = expectedHex,
            ActualHex = actualHex,
            ReplacementHex = replacementHex,
            ExpectedBytes = expected,
            ReplacementBytes = replacement,
            Matches = matches
        };
    }

    private static ZonePoolPatchManifestResult Failed(
        string detail,
        ZoneHandleRebasePlan layout,
        string baselineHash = "",
        ZoneBinaryPatchPreflightResult? core = null,
        ZoneBinaryDependencyAuditResult? dependencies = null,
        IReadOnlyList<ZonePoolPatchManifestSite>? sites = null)
        => new()
        {
            Layout = layout,
            Core = core,
            Dependencies = dependencies,
            ManifestVerified = false,
            NoOverlaps = false,
            RollbackVerified = false,
            BaselineSha256 = baselineHash,
            Sites = sites ?? Array.Empty<ZonePoolPatchManifestSite>(),
            ChangedSites = sites?.Where(x => x.WouldChange).ToArray() ?? Array.Empty<ZonePoolPatchManifestSite>(),
            Detail = detail
        };
}

public sealed class ZonePoolPatchManifestResult
{
    public ZoneHandleRebasePlan Layout { get; init; } = new();
    public ZoneBinaryPatchPreflightResult? Core { get; init; }
    public ZoneBinaryDependencyAuditResult? Dependencies { get; init; }
    public bool ManifestVerified { get; init; }
    public bool NoOverlaps { get; init; }
    public bool RollbackVerified { get; init; }
    public string BaselineSha256 { get; init; } = string.Empty;
    public string ProspectivePatchedSha256 { get; init; } = string.Empty;
    public string RollbackSha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ZonePoolPatchManifestSite> Sites { get; init; } = Array.Empty<ZonePoolPatchManifestSite>();
    public IReadOnlyList<ZonePoolPatchManifestSite> ChangedSites { get; init; } = Array.Empty<ZonePoolPatchManifestSite>();
}

public sealed class ZonePoolPatchManifestSite
{
    public string Source { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public uint VirtualAddress { get; init; }
    public int FileOffset { get; init; }
    public string ExpectedHex { get; init; } = string.Empty;
    public string ActualHex { get; init; } = string.Empty;
    public string ReplacementHex { get; init; } = string.Empty;
    public byte[] ExpectedBytes { get; init; } = Array.Empty<byte>();
    public byte[] ReplacementBytes { get; init; } = Array.Empty<byte>();
    public bool Matches { get; init; }
    public bool WouldChange => ExpectedHex != ReplacementHex;
}
