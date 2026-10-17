using System.Diagnostics;
using System.Security.Cryptography;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Compensates a rollback whose file exchange succeeded but metadata commit failed.
/// Only restores the exact previous image from its audit while the original metadata
/// and the expected rolled-back image are still present. Never starts a process.
/// </summary>
internal static class ZoneRollbackRecovery
{
    internal sealed record Result(bool Success, string Detail);

    public static Result RestorePreviousFile(
        string target, string audit, string previousSha256, string rolledBackSha256,
        string metadataPath, byte[] originalMetadata)
    {
        var stage = target + ".nextgen-rollback-recovery-" + Guid.NewGuid().ToString("N");
        try
        {
            var metadataHash = Convert.ToHexString(SHA256.HashData(originalMetadata));
            void VerifyCurrentState()
            {
                EnsureNoZones();
                if (!HashMatches(target, rolledBackSha256)
                    || !HashMatches(audit, previousSha256)
                    || !HashMatches(metadataPath, metadataHash))
                    throw new IOException("Datei, Audit oder Metadaten wurden verändert; automatische Rücknahme verweigert.");
            }

            VerifyCurrentState();
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                var bytes = File.ReadAllBytes(audit);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (!HashMatches(stage, previousSha256))
                throw new IOException("Wiederherstellungsdatei entspricht nicht dem vorherigen SHA.");

            VerifyCurrentState();
            File.Replace(stage, target, null, ignoreMetadataErrors: false);
            if (!HashMatches(target, previousSha256) || !HashMatches(metadataPath, metadataHash))
                throw new IOException("Abschließende Prüfung des wiederhergestellten Zustands fehlgeschlagen.");

            return new Result(true, "ROLLBACK ZURÜCKGENOMMEN: Vorheriger Dateistand und unveränderte Metadaten sind konsistent; Audit erhalten. Rollback nach Behebung der Dateisperre erneut ausführen.");
        }
        catch (Exception ex)
        {
            return new Result(false, "ROLLBACK-RÜCKNAHME BLOCKIERT: " + ex.Message + " Server nicht starten; Datei-/Metadatenzustand prüfen.");
        }
        finally
        {
            try { if (File.Exists(stage)) File.Delete(stage); } catch { }
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoZones()
    {
        var processes = Process.GetProcessesByName("Zone");
        try
        {
            if (processes.Length != 0)
                throw new IOException("Mindestens eine Zone läuft; keine Datei wird ersetzt.");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
