namespace NextGen.Fiesta.ServerManager.Models;

public sealed class CapacityLimitEntry
{
    public string Scope { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int HardLimit { get; set; }
    public int ObjectSizeBytes { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public string LimitText => HardLimit.ToString("N0");
    public string ObjectSizeText => ObjectSizeBytes > 0 ? $"{ObjectSizeBytes:N0} B" : "–";
    public string PoolMemoryText => ObjectSizeBytes > 0 && HardLimit > 0
        ? $"{(HardLimit * (double)ObjectSizeBytes / 1024d / 1024d):N1} MiB"
        : "–";
}
