namespace NextGen.Fiesta.ServerManager.Models;

public sealed record ServerInfoEntry(
    string Name,
    int ServerType,
    int WorldNo,
    int ZoneNo,
    int ConnectionKind,
    string Host,
    int Port,
    int BackLog,
    int MaxAccept);
