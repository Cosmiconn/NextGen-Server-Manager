using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Local Wireshark/dumpcap capture helper for the controlled NA2016 player load test.
/// It never interprets credentials and never uploads captures. Raw captures remain sensitive
/// because the protocol handshake allows client traffic to be decrypted offline.
/// </summary>
public sealed class FiestaPacketCaptureRecorder
{
    private static readonly Regex InterfaceRegex = new(
        @"^(?<index>\d+)\.\s+(?<device>\S+)(?:\s+\((?<friendly>.*)\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<FiestaCaptureInterface> ListInterfaces(string? dumpcapPath = null)
    {
        var dumpcap = ResolveDumpcap(dumpcapPath);
        var output = RunProcess(dumpcap, "-D");
        return ParseInterfaces(output);
    }

    public FiestaPacketCaptureSession Start(FiestaPacketCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var dumpcap = ResolveDumpcap(options.DumpcapPath);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(outputPath))
            throw new IOException($"Capture-Zieldatei existiert bereits: {outputPath}");

        var filter = BuildServerPortFilter(options.LoginPort, options.ZonePort);
        var stderr = new StringBuilder();
        var sync = new object();

        var psi = new ProcessStartInfo
        {
            FileName = dumpcap,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(options.InterfaceSelector);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(filter);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add(outputPath);
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add($"duration:{Math.Clamp(options.MaxDurationSeconds, 15, 900)}");
        psi.ArgumentList.Add("-q");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (sync)
            {
                if (stderr.Length > 16_384)
                    stderr.Remove(0, Math.Min(stderr.Length, 8192));
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("dumpcap.exe konnte nicht gestartet werden.");
            process.BeginErrorReadLine();

            if (process.WaitForExit(450))
            {
                string error;
                lock (sync) error = stderr.ToString().Trim();
                throw new InvalidOperationException(
                    $"dumpcap.exe beendete sich direkt mit ExitCode {process.ExitCode}: {error}");
            }

            return new FiestaPacketCaptureSession(process, outputPath, filter, stderr, sync);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public static FiestaPacketCaptureSelfTestResult RunSelfTest()
    {
        try
        {
            const string sample =
                "1. \\Device\\NPF_{01234567-89AB-CDEF-0123-456789ABCDEF} (Ethernet)\n" +
                "2. \\Device\\NPF_Loopback (Adapter for loopback traffic capture)\n" +
                "3. etwdump (Event Tracing for Windows (ETW) reader)\n";

            var interfaces = ParseInterfaces(sample);
            if (interfaces.Count != 3
                || interfaces[0].Selector != "1"
                || interfaces[1].Selector != "2"
                || !interfaces[1].DisplayName.Contains("loopback", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("dumpcap -D Interface-Liste wird nicht korrekt geparst.");
            }

            var filter = BuildServerPortFilter(9010, 9016);
            if (!string.Equals(filter, "tcp portrange 9000-9100", StringComparison.Ordinal))
                throw new InvalidDataException("Standard-Fiesta-Capturefilter ist unerwartet: " + filter);

            var outside = BuildServerPortFilter(8123, 9200);
            if (!outside.Contains("tcp port 8123", StringComparison.Ordinal)
                || !outside.Contains("tcp port 9200", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Ports außerhalb 9000-9100 werden nicht zusätzlich erfasst.");
            }

            return new FiestaPacketCaptureSelfTestResult(
                true,
                "PACKET CAPTURE SELFTEST: PASS · dumpcap interface parser + Fiesta port filter.");
        }
        catch (Exception ex)
        {
            return new FiestaPacketCaptureSelfTestResult(
                false,
                "PACKET CAPTURE SELFTEST: FAIL · " + ex.Message);
        }
    }

    internal static IReadOnlyList<FiestaCaptureInterface> ParseInterfaces(string text)
    {
        var result = new List<FiestaCaptureInterface>();
        using var reader = new StringReader(text ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = InterfaceRegex.Match(line.Trim());
            if (!match.Success) continue;

            var index = int.Parse(match.Groups["index"].Value);
            var selector = index.ToString();
            var device = match.Groups["device"].Value;
            var friendly = match.Groups["friendly"].Success
                ? match.Groups["friendly"].Value.Trim()
                : string.Empty;
            var display = string.IsNullOrWhiteSpace(friendly)
                ? $"{selector} · {device}"
                : $"{selector} · {friendly} · {device}";

            result.Add(new FiestaCaptureInterface(selector, device, friendly, display));
        }

        return result;
    }

    internal static string BuildServerPortFilter(int loginPort, int zonePort)
    {
        if (loginPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(loginPort));
        if (zonePort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(zonePort));

        var clauses = new List<string> { "tcp portrange 9000-9100" };
        foreach (var port in new[] { loginPort, zonePort }.Distinct())
        {
            if (port is < 9000 or > 9100)
                clauses.Add($"tcp port {port}");
        }

        return string.Join(" or ", clauses);
    }

    private static string ResolveDumpcap(string? requestedPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestedPath))
            candidates.Add(requestedPath);

        candidates.Add("dumpcap.exe");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "Wireshark", "dumpcap.exe"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!candidate.Equals("dumpcap.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            if (candidate.Equals("dumpcap.exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var version = RunProcess(candidate, "--version");
                    if (version.Contains("Dumpcap", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch { }
            }
        }

        throw new FileNotFoundException(
            "dumpcap.exe wurde nicht gefunden. Wireshark/Npcap installieren; der Manager lädt oder installiert keine Capture-Treiber selbst.");
    }

    private static string RunProcess(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"'{fileName}' konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"'{fileName}' lief länger als 15 Sekunden.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{fileName}' ExitCode {process.ExitCode}: {stderr.Trim()}");
        return stdout;
    }
}

public sealed class FiestaPacketCaptureSession : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr;
    private readonly object _sync;
    private int _stopped;

    internal FiestaPacketCaptureSession(
        Process process,
        string outputPath,
        string filter,
        StringBuilder stderr,
        object sync)
    {
        _process = process;
        OutputPath = outputPath;
        Filter = filter;
        _stderr = stderr;
        _sync = sync;
        StartedUtc = DateTimeOffset.UtcNow;
    }

    public string OutputPath { get; }
    public string Filter { get; }
    public DateTimeOffset StartedUtc { get; }
    public int ProcessId => _process.Id;
    public bool IsRunning
    {
        get
        {
            try { return !_process.HasExited; }
            catch { return false; }
        }
    }

    public FiestaPacketCaptureStopResult Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return BuildResult(false);

        var forced = false;
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    if (_process.CloseMainWindow())
                        _process.WaitForExit(1500);
                }
                catch { }

                if (!_process.HasExited)
                {
                    forced = true;
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
        }
        catch (Exception ex)
        {
            return new FiestaPacketCaptureStopResult(
                false,
                OutputPath,
                forced,
                SafeLength(OutputPath),
                "Capture-Prozess konnte nicht sauber beendet werden: " + ex.Message);
        }

        return BuildResult(forced);
    }

    private FiestaPacketCaptureStopResult BuildResult(bool forced)
    {
        var length = SafeLength(OutputPath);
        string diagnostics;
        lock (_sync) diagnostics = _stderr.ToString().Trim();

        if (length < 32)
        {
            return new FiestaPacketCaptureStopResult(
                false,
                OutputPath,
                forced,
                length,
                $"Capture enthält keine verwertbaren Daten ({length} Byte). dumpcap: {diagnostics}");
        }

        return new FiestaPacketCaptureStopResult(
            true,
            OutputPath,
            forced,
            length,
            $"CAPTURE STOPPED · {length:N0} Byte · {(forced ? "früh beendet; Import validiert PCAPNG" : "dumpcap regulär beendet")}.");
    }

    private static long SafeLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        _process.Dispose();
    }
}

public sealed class FiestaPacketCaptureOptions
{
    public string InterfaceSelector { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public int LoginPort { get; init; } = 9010;
    public int ZonePort { get; init; } = 9016;
    public int MaxDurationSeconds { get; init; } = 180;
    public string? DumpcapPath { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(InterfaceSelector))
            throw new ArgumentException("Capture-Interface fehlt.");
        if (string.IsNullOrWhiteSpace(OutputPath))
            throw new ArgumentException("Capture-Ausgabepfad fehlt.");
        if (LoginPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(LoginPort));
        if (ZonePort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(ZonePort));
        if (MaxDurationSeconds is < 15 or > 900)
            throw new ArgumentOutOfRangeException(nameof(MaxDurationSeconds));
    }
}

public sealed record FiestaCaptureInterface(
    string Selector,
    string DeviceName,
    string FriendlyName,
    string DisplayName);

public readonly record struct FiestaPacketCaptureStopResult(
    bool Success,
    string CapturePath,
    bool ForcedTermination,
    long SizeBytes,
    string Detail);

public readonly record struct FiestaPacketCaptureSelfTestResult(bool Success, string Detail);
