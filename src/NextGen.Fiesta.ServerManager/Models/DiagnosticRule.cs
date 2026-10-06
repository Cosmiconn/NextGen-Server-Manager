namespace NextGen.Fiesta.ServerManager.Models;

public sealed class DiagnosticRule
{
    public string Code { get; set; } = string.Empty;
    public string Severity { get; set; } = "Warning";
    public string Pattern { get; set; } = string.Empty;
    public bool Regex { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public string RepairAction { get; set; } = "None";
}
