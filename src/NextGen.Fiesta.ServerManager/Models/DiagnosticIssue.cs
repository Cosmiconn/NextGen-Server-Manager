using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NextGen.Fiesta.ServerManager.Models;

public sealed class DiagnosticIssue : INotifyPropertyChanged
{
    public required string Code { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Recommendation { get; init; }
    public required string Source { get; init; }
    public string? ServiceName { get; init; }
    public int? ZoneNumber { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string? Evidence { get; init; }
    public double Confidence { get; init; } = 1.0;
    public RepairActionKind RepairAction { get; init; } = RepairActionKind.None;
    public bool IsAutoRepairable => RepairAction != RepairActionKind.None;
    public string SeverityText => Severity.ToString();
    public string ConfidenceText => $"{Confidence:P0}";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
