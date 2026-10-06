namespace NextGen.Fiesta.ServerManager.Models;

public sealed class MapCapacityEntry
{
    public string MapId { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
    public string Zones { get; set; } = string.Empty;
    public int? FieldX { get; set; }
    public int? FieldY { get; set; }
    public int? ShbdXBytes { get; set; }
    public int? ShbdYRows { get; set; }
    public long? PayloadBytes { get; set; }
    public long FileBytes { get; set; }
    public bool HasShbd { get; set; }
    public bool PayloadValid { get; set; }
    public string ShbdPath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    public long? CollisionWidthCells => ShbdXBytes is int x && x >= 0 ? (long)x * 8L : null;
    public long? CollisionHeightCells => ShbdYRows;
    public decimal? ServerBlockWorldX => ShbdXBytes is int x && x >= 0 ? x * 50m : null;
    public decimal? ServerBlockWorldY => ShbdYRows is int y && y >= 0 ? y * 6.25m : null;
    public decimal? FieldWorldX => FieldX is int x && x >= 0 ? x * 50m : null;
    public decimal? FieldWorldY => FieldY is int y && y >= 0 ? y * 50m : null;

    public string FieldSizeText => FieldX is int x && FieldY is int y ? $"{x:N0} × {y:N0}" : "–";
    public string FieldWorldSizeText => FieldWorldX is decimal x && FieldWorldY is decimal y ? $"{x:N0} × {y:N0}" : "–";
    public string CollisionGridText => CollisionWidthCells is long x && CollisionHeightCells is long y ? $"{x:N0} × {y:N0}" : "–";
    public string ServerBlockWorldSizeText => ServerBlockWorldX is decimal x && ServerBlockWorldY is decimal y ? $"{x:N0} × {y:N0}" : "–";
    public string ShbdHeaderText => ShbdXBytes is int x && ShbdYRows is int y ? $"{x:N0} × {y:N0}" : "–";
    public string FileSizeText => HasShbd ? $"{FileBytes / 1024d / 1024d:N2} MiB" : "–";
}
