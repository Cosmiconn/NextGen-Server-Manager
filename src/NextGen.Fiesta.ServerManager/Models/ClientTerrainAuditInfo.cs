namespace NextGen.Fiesta.ServerManager.Models;

public sealed class ClientTerrainAuditInfo
{
    public string ClientBinaryPath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string PeKind { get; set; } = "–";
    public bool LargeAddressAware { get; set; }
    public bool KnownNa2016Baseline { get; set; }
    public int TerrainSignatureCount { get; set; }
    public bool HasTerrainSignatures { get; set; }
    public string StaticLoaderEvidence { get; set; } = string.Empty;
    public string CompatibilityAssessment { get; set; } = string.Empty;

    public string BinaryName => string.IsNullOrWhiteSpace(ClientBinaryPath) ? "Fiesta.bin nicht gefunden" : Path.GetFileName(ClientBinaryPath);
    public string HashShort => string.IsNullOrWhiteSpace(Sha256) ? "–" : Sha256[..Math.Min(16, Sha256.Length)] + "…";
    public string LaaText => Exists ? (LargeAddressAware ? "JA" : "NEIN") : "–";
    public string BaselineText => KnownNa2016Baseline ? "NA2016-Baseline ✓" : Exists ? "unbekannter/abweichender Build" : "–";
    public string SignatureText => Exists ? $"{TerrainSignatureCount}/10" : "–";
}
