namespace NextGen.Fiesta.ServerManager.Models;

public sealed record LogEvent(
    DateTime Timestamp,
    string Source,
    string Message,
    DiagnosticSeverity Severity,
    string? Code = null,
    int? Department = null,
    int? Command = null,
    int? PacketLength = null);
