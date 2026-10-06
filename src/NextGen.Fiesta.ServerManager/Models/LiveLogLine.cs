namespace NextGen.Fiesta.ServerManager.Models;

public sealed record LiveLogLine(
    DateTime Timestamp,
    string ServiceName,
    int? ZoneNumber,
    string Path,
    string Line);
