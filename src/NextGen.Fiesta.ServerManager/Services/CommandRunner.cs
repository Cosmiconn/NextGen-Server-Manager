using System.Diagnostics;
using System.Text;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public sealed class CommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return new CommandResult(-1, stdout.ToString(), stderr + $"Command timeout after {timeoutMs} ms");
        }

        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public Process? StartDetached(string fileName, string? arguments = null, string? workingDirectory = null, bool useShellExecute = false, string? verb = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
            UseShellExecute = useShellExecute
        };
        if (!string.IsNullOrWhiteSpace(verb)) psi.Verb = verb;
        return Process.Start(psi);
    }
}
