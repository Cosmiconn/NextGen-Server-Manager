namespace NextGen.Fiesta.ServerManager.Models;

/// <summary>
/// Read-only occupancy of the three primary Zone object pools. Values are considered
/// authoritative only when RuntimeVerified is true; the probe is tied to the exact
/// NA2016 Zone.exe baseline hash and validated list maxima.
/// </summary>
public sealed class ZoneObjectPoolRuntimeSnapshot
{
    public bool RuntimeVerified { get; init; }
    public int PlayerCount { get; init; }
    public int PlayerLimit { get; init; } = 1500;
    public int MobCount { get; init; }
    public int MobLimit { get; init; } = 8000;
    public int NpcCount { get; init; }
    public int NpcLimit { get; init; } = 256;
    public string Detail { get; init; } = string.Empty;

    public string CompactText => RuntimeVerified
        ? $"P {PlayerCount:N0}/{PlayerLimit:N0} · M {MobCount:N0}/{MobLimit:N0} · N {NpcCount:N0}/{NpcLimit:N0}"
        : "Poolbelegung nicht verifiziert";
}
