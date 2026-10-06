namespace NextGen.Fiesta.ServerManager.Models;

public sealed class ClientTerrainProfileEntry
{
    public int TerrainQuads { get; set; }
    public int HeightPoints { get; set; }
    public long HeightPointCount { get; set; }
    public long WorldSideUnits { get; set; }
    public int ChunkCount { get; set; }
    public long RawHeightArraysBytes { get; set; }
    public long HtdPayloadBytes { get; set; }
    public long ShbdPayloadBytes { get; set; }
    public long CollisionGridSide { get; set; }
    public bool FitsSigned16WorldSide { get; set; }
    public string Risk { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;

    public string TerrainText => $"{TerrainQuads:N0} × {TerrainQuads:N0}";
    public string HeightMapText => $"{HeightPoints:N0} × {HeightPoints:N0}";
    public string WorldText => $"{WorldSideUnits:N0} × {WorldSideUnits:N0}";
    public string CollisionText => $"{CollisionGridSide:N0} × {CollisionGridSide:N0}";
    public string RawHeightMemoryText => $"{RawHeightArraysBytes / 1024d / 1024d:N2} MiB";
    public string HtdSizeText => $"{HtdPayloadBytes / 1024d / 1024d:N2} MiB";
    public string ShbdSizeText => $"{ShbdPayloadBytes / 1024d / 1024d:N2} MiB";
    public string Signed16Text => FitsSigned16WorldSide ? "ja" : "NEIN";
}
