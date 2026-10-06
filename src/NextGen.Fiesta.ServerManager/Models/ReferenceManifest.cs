namespace NextGen.Fiesta.ServerManager.Models;

public sealed class ReferenceManifest
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, ReferenceFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReferenceFile
{
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}
