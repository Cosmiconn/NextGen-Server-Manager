namespace NextGen.Fiesta.ServerManager.Models;

public sealed class ClientTerrainMapEntry
{
    public string MapName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public int WidthPoints { get; set; }
    public int HeightPoints { get; set; }
    public double BlockWidth { get; set; }
    public double BlockHeight { get; set; }
    public int QuadsWide { get; set; }
    public int QuadsHigh { get; set; }
    public string Risk { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public int TerrainQuadsX => Math.Max(0, WidthPoints - 1);
    public int TerrainQuadsY => Math.Max(0, HeightPoints - 1);
    public double WorldX => TerrainQuadsX * BlockWidth;
    public double WorldY => TerrainQuadsY * BlockHeight;
    public int ChunkCountX => QuadsWide > 0 ? (TerrainQuadsX + QuadsWide - 1) / QuadsWide : 0;
    public int ChunkCountY => QuadsHigh > 0 ? (TerrainQuadsY + QuadsHigh - 1) / QuadsHigh : 0;
    public int ChunkCount => ChunkCountX * ChunkCountY;

    public string PointsText => $"{WidthPoints:N0} × {HeightPoints:N0}";
    public string QuadsText => $"{TerrainQuadsX:N0} × {TerrainQuadsY:N0}";
    public string BlockText => $"{BlockWidth:N1} × {BlockHeight:N1}";
    public string WorldText => $"{WorldX:N0} × {WorldY:N0}";
    public string ChunkText => QuadsWide > 0 && QuadsHigh > 0 ? $"{ChunkCountX}×{ChunkCountY} = {ChunkCount:N0}" : "–";
}
