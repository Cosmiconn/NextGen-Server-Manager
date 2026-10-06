using System.Diagnostics;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed record PdbStatus(string Name, string Path, bool Exists, bool Indexed, string Detail);

public sealed class PdbSymbolService
{
    private readonly CommandRunner _runner;
    public PdbSymbolService(CommandRunner runner) => _runner = runner;

    public async Task<string?> FindLlvmPdbUtilAsync(CancellationToken ct = default)
    {
        // 1) Explicit override for portable/custom installations.
        foreach (var variable in new[] { "NEXTGEN_LLVM_PDBUTIL", "LLVM_PDBUTIL" })
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return Path.GetFullPath(configured);
        }

        // 2) PATH lookup. This is fast when LLVM was installed before the current process started.
        try
        {
            var where = await _runner.RunAsync("where.exe", "llvm-pdbutil.exe", timeoutMs: 5000, cancellationToken: ct);
            if (where.Success)
            {
                var path = where.StdOut
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
        }
        catch
        {
            // Continue with deterministic fallback locations below.
        }

        // 3) Search PATH ourselves. This also copes with shells where where.exe behaves unexpectedly.
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "llvm-pdbutil.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }

        // 4) Common official LLVM install locations. German Explorer displays
        //    C:\Program Files as "C:\Programme", but Environment.SpecialFolder.ProgramFiles
        //    resolves the real filesystem path correctly.
        var candidates = new List<string>();
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"LLVM\bin\llvm-pdbutil.exe");
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"LLVM\bin\llvm-pdbutil.exe");
        AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\LLVM\bin\llvm-pdbutil.exe");

        var chocolatey = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (!string.IsNullOrWhiteSpace(chocolatey))
            AddCandidate(candidates, chocolatey, @"bin\llvm-pdbutil.exe");

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        return null;
    }

    public async Task<PdbStatus> IndexAsync(string pdbPath, string cacheRoot, CancellationToken ct = default)
    {
        if (!File.Exists(pdbPath))
            return new PdbStatus(Path.GetFileName(pdbPath), pdbPath, false, false, "PDB fehlt");

        var tool = await FindLlvmPdbUtilAsync(ct);
        if (tool is null)
            return new PdbStatus(Path.GetFileName(pdbPath), pdbPath, true, false,
                "llvm-pdbutil.exe nicht gefunden. Geprüft wurden PATH, Program Files\\LLVM\\bin und lokale LLVM-Installationen.");

        Directory.CreateDirectory(cacheRoot);

        // Every Zone folder contains a file named Zone.pdb. Do not let Zone00..ZoneNN
        // overwrite one another in the symbol cache.
        var parentName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(pdbPath))) ?? "root";
        var cacheName = $"{SanitizeFileName(parentName)}__{SanitizeFileName(Path.GetFileNameWithoutExtension(pdbPath))}.symbols.txt";
        var outPath = Path.Combine(cacheRoot, cacheName);

        var result = await _runner.RunAsync(tool, $"dump -types -symbols \"{pdbPath}\"", timeoutMs: 120000, cancellationToken: ct);
        if (!result.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
            return new PdbStatus(Path.GetFileName(pdbPath), pdbPath, true, false, detail);
        }

        await File.WriteAllTextAsync(outPath, result.StdOut, ct);
        return new PdbStatus(Path.GetFileName(pdbPath), pdbPath, true, true, $"{outPath} · Tool: {tool}");
    }

    public IEnumerable<string> SearchIndex(string indexPath, string term, int max = 50)
    {
        if (!File.Exists(indexPath) || string.IsNullOrWhiteSpace(term)) yield break;
        var count = 0;
        foreach (var line in File.ReadLines(indexPath))
        {
            if (!line.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            yield return line.Trim();
            if (++count >= max) yield break;
        }
    }

    private static void AddCandidate(ICollection<string> candidates, string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        try { candidates.Add(Path.Combine(root, relative)); } catch { }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value;
    }
}
