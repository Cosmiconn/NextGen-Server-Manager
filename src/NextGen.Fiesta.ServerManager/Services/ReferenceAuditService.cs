using System.Security.Cryptography;
using System.Text.Json;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed class ReferenceAuditService
{
    private readonly ReferenceManifest? _manifest;
    public ReferenceAuditService()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "reference-na2016.json");
        if (!File.Exists(path)) return;
        try
        {
            _manifest = JsonSerializer.Deserialize<ReferenceManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { }
    }

    public IReadOnlyList<DiagnosticIssue> Audit(string root, IReadOnlyList<FiestaServiceEntry> services)
    {
        var issues = new List<DiagnosticIssue>();
        if (_manifest is null) return issues;

        foreach (var svc in services)
        {
            if (!File.Exists(svc.ExecutablePath)) continue;
            string? referenceRel = svc.Kind switch
            {
                FiestaServiceKind.Account => "Account/Account.exe",
                FiestaServiceKind.AccountLog => "AccountLog/AccountLog.exe",
                FiestaServiceKind.Login => "Login/Login.exe",
                FiestaServiceKind.Character => "Character/Character.exe",
                FiestaServiceKind.GameLog => "GameLog/GameLog.exe",
                FiestaServiceKind.WorldManager => "WorldManager/WorldManager.exe",
                FiestaServiceKind.Zone => "Zone00/Zone.exe",
                _ => null
            };
            if (referenceRel is null || !_manifest.Files.TryGetValue(referenceRel, out var reference)) continue;
            var actual = Sha256(svc.ExecutablePath);
            if (!actual.Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new DiagnosticIssue
                {
                    Code = "NG-REF-0001",
                    Severity = DiagnosticSeverity.Warning,
                    Title = "Binary weicht von NA2016-Referenz ab",
                    Description = $"{svc.DisplayName} verwendet eine andere EXE als die mitgelieferte geprüfte NA2016-Referenz.",
                    Recommendation = "Nur dann ersetzen, wenn die Abweichung nicht absichtlich ist. Vorher Binary sichern und Herkunft/Version klären.",
                    Source = svc.ExecutablePath,
                    ServiceName = svc.ServiceName,
                    ZoneNumber = svc.ZoneNumber,
                    Evidence = $"Ist SHA256={actual}; Referenz={reference.Sha256}",
                    Confidence = 1.0
                });
            }
        }

        // All zone binaries should normally be byte-identical in this NA2016 set.
        var zoneGroups = services.Where(x => x.Kind == FiestaServiceKind.Zone && File.Exists(x.ExecutablePath))
            .Select(x => new { Service = x, Hash = Sha256(x.ExecutablePath) })
            .GroupBy(x => x.Hash, StringComparer.OrdinalIgnoreCase).ToList();
        if (zoneGroups.Count > 1)
        {
            issues.Add(new DiagnosticIssue
            {
                Code = "NG-REF-0002",
                Severity = DiagnosticSeverity.Error,
                Title = "Zone.exe Versionsmix",
                Description = $"Die installierten ZoneServer verwenden {zoneGroups.Count} unterschiedliche Zone.exe-Builds.",
                Recommendation = "Prüfen, ob der Mix beabsichtigt ist. Für stock NA2016 sollten Zone00..ZoneNN dieselbe Binary verwenden.",
                Source = root,
                Evidence = string.Join(" | ", zoneGroups.Select(g => $"{g.Key[..Math.Min(12, g.Key.Length)]}: {string.Join(",", g.Select(x => x.Service.DisplayName))}")),
                Confidence = 1.0
            });
        }
        return issues;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
