using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Imports one real NA2016 client -> Zone CH6/1 packet from a Wireshark capture.
/// tshark performs TCP reassembly; this importer applies the verified Fiesta framing/XOR rules,
/// keeps the complete original transfer packet and only records the two fields that are safe
/// to materialize per simulated client: RandomID and character name.
/// </summary>
public sealed class FiestaZoneTransferCaptureImporter
{
    private static readonly Regex NodeRegex = new(
        @"^Node\s+(?<node>[01]):\s+(?<endpoint>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HexRegex = new(
        @"^[0-9a-fA-F]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FiestaZoneTransferCaptureResult Import(FiestaZoneTransferCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var tshark = ResolveTshark(options.TsharkPath);
        var streams = DiscoverCandidateStreams(tshark, options.CapturePath, options.ZonePort);
        if (streams.Count == 0)
            return FiestaZoneTransferCaptureResult.Blocked(
                $"Im Mitschnitt wurde kein TCP-Stream mit Zone-Port {options.ZonePort} gefunden.");

        var candidates = new List<FiestaZoneTransferCandidate>();
        foreach (var streamId in streams)
        {
            var follow = RunTshark(
                tshark,
                "-r", options.CapturePath,
                "-q",
                "-z", $"follow,tcp,raw,{streamId}");

            var parsed = TryExtractFromFollowText(
                follow,
                streamId,
                options.ZonePort,
                expectedCharacterName: null);

            if (parsed is not null)
                candidates.Add(parsed);
        }

        if (candidates.Count == 0)
        {
            return FiestaZoneTransferCaptureResult.Blocked(
                $"Keiner der {streams.Count} Zone-TCP-Streams enthielt ein vollständig entschlüsselbares CH6/1-Transferpaket. " +
                "Der Mitschnitt muss beendet sein und den vollständigen Zone-Verbindungsaufbau inklusive SH2/7-Handshake und Client→Zone-Transfer enthalten.");
        }

        FiestaZoneTransferCandidate selected;
        if (!string.IsNullOrWhiteSpace(options.ExpectedCharacterName))
        {
            var exact = candidates
                .Where(x => string.Equals(
                    x.CharacterName,
                    options.ExpectedCharacterName,
                    StringComparison.Ordinal))
                .ToList();

            if (exact.Count != 1)
            {
                return FiestaZoneTransferCaptureResult.Blocked(
                    exact.Count == 0
                        ? $"CH6/1 wurde gefunden, aber nicht für Charakter '{options.ExpectedCharacterName}'. Gefunden: " +
                          string.Join(", ", candidates.Select(x => x.CharacterName))
                        : $"Mehrere CH6/1-Transfers für '{options.ExpectedCharacterName}' gefunden. Bitte einen Mitschnitt mit genau einem Login verwenden.");
            }

            selected = exact[0];
        }
        else
        {
            if (candidates.Count != 1)
            {
                return FiestaZoneTransferCaptureResult.Blocked(
                    $"Es wurden {candidates.Count} gültige CH6/1-Transfers gefunden. ExpectedCharacterName angeben, um eindeutig auszuwählen.");
            }

            selected = candidates[0];
        }

        var template = new FiestaZoneTransferTemplate
        {
            Format = FiestaZoneTransferTemplate.FormatV1,
            PayloadBase64 = Convert.ToBase64String(selected.DecryptedPayload),
            RandomIdOffset = 2,
            CharacterNameOffset = 4,
            CharacterNameLength = 16,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(selected.DecryptedPayload))
        };

        template.Validate();

        var output = Path.GetFullPath(options.OutputTemplatePath);
        var directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            template,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json + Environment.NewLine, new UTF8Encoding(false));

        return new FiestaZoneTransferCaptureResult
        {
            Success = true,
            TemplatePath = output,
            TcpStreamId = selected.TcpStreamId,
            XorPosition = selected.XorPosition,
            RandomId = selected.RandomId,
            CharacterName = selected.CharacterName,
            PayloadLength = selected.DecryptedPayload.Length,
            PayloadSha256 = template.SourceSha256 ?? string.Empty,
            Detail =
                $"ZONE TRANSFER CAPTURE: SUCCESS · tcp.stream={selected.TcpStreamId} · " +
                $"CH6/1 {selected.DecryptedPayload.Length} Byte · XOR={selected.XorPosition} · " +
                $"RandomID={selected.RandomId} · Charakter={selected.CharacterName}."
        };
    }

    public static FiestaZoneTransferCaptureSelfTestResult RunSelfTest()
    {
        try
        {
            const ushort xorPosition = 123;
            const ushort randomId = 0x3456;
            const string character = "LoadProbe";

            var handshakePayload = new byte[4];
            var handshakeOpcode = FiestaHeadlessLoadClient.PackOpcode(2, 7);
            handshakePayload[0] = (byte)(handshakeOpcode & 0xff);
            handshakePayload[1] = (byte)(handshakeOpcode >> 8);
            handshakePayload[2] = (byte)(xorPosition & 0xff);
            handshakePayload[3] = (byte)(xorPosition >> 8);

            var transferBody = new byte[1590];
            transferBody[0] = (byte)(randomId & 0xff);
            transferBody[1] = (byte)(randomId >> 8);
            FiestaHeadlessLoadClient.WriteFixedAscii(transferBody.AsSpan(2, 16), character);
            for (var i = 18; i < transferBody.Length; i++)
                transferBody[i] = unchecked((byte)(i * 31 + 7));

            var transferPayload = new byte[1592];
            var transferOpcode = FiestaHeadlessLoadClient.PackOpcode(6, 1);
            transferPayload[0] = (byte)(transferOpcode & 0xff);
            transferPayload[1] = (byte)(transferOpcode >> 8);
            transferBody.CopyTo(transferPayload.AsSpan(2));

            var encrypted = transferPayload.ToArray();
            var cipher = new FiestaXorCipher(xorPosition);
            cipher.TransformInPlace(encrypted);

            var serverFrame = FiestaWireConnection.FramePayload(handshakePayload);
            var clientFrame = FiestaWireConnection.FramePayload(encrypted);

            var follow =
                "===================================================================\n" +
                "Follow: tcp,raw\n" +
                "Filter: tcp.stream eq 17\n" +
                "Node 0: 127.0.0.1:55000\n" +
                "Node 1: 127.0.0.1:9016\n" +
                Convert.ToHexString(clientFrame).ToLowerInvariant() + "\n" +
                "\t" + Convert.ToHexString(serverFrame).ToLowerInvariant() + "\n" +
                "===================================================================\n";

            var parsed = TryExtractFromFollowText(follow, 17, 9016, expectedCharacterName: null)
                         ?? throw new InvalidDataException("Synthetischer CH6/1-Transfer wurde ohne vorgegebenen Charakternamen nicht extrahiert.");

            if (parsed.XorPosition != xorPosition
                || parsed.RandomId != randomId
                || parsed.CharacterName != character
                || !parsed.DecryptedPayload.SequenceEqual(transferPayload))
            {
                throw new InvalidDataException("Capture-Importer veränderte Handshake, RandomID, Name oder Transferbytes.");
            }

            return new FiestaZoneTransferCaptureSelfTestResult(
                true,
                "ZONE TRANSFER CAPTURE SELFTEST: PASS · tshark-follow parser, XOR, Auto-Char-Erkennung und 1592-Byte CH6/1 roundtrip.");
        }
        catch (Exception ex)
        {
            return new FiestaZoneTransferCaptureSelfTestResult(
                false,
                "ZONE TRANSFER CAPTURE SELFTEST: FAIL · " + ex.Message);
        }
    }

    internal static FiestaZoneTransferCandidate? TryExtractFromFollowText(
        string followText,
        int streamId,
        int zonePort,
        string? expectedCharacterName)
    {
        if (string.IsNullOrWhiteSpace(followText))
            return null;

        var nodePorts = new Dictionary<int, int>();
        var nodeData = new Dictionary<int, MemoryStream>
        {
            [0] = new MemoryStream(),
            [1] = new MemoryStream()
        };

        using var reader = new StringReader(followText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var nodeMatch = NodeRegex.Match(line.Trim());
            if (nodeMatch.Success)
            {
                var node = int.Parse(nodeMatch.Groups["node"].Value, CultureInfo.InvariantCulture);
                var endpoint = nodeMatch.Groups["endpoint"].Value.Trim();
                var lastColon = endpoint.LastIndexOf(':');
                if (lastColon >= 0
                    && int.TryParse(endpoint[(lastColon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                {
                    nodePorts[node] = port;
                }
                continue;
            }

            var nodeIndex = line.StartsWith('\t') ? 1 : 0;
            var candidate = line.Trim();
            if (candidate.Length == 0
                || candidate.Length % 2 != 0
                || !HexRegex.IsMatch(candidate))
            {
                continue;
            }

            var bytes = Convert.FromHexString(candidate);
            nodeData[nodeIndex].Write(bytes);
        }

        var serverNode = nodePorts.FirstOrDefault(x => x.Value == zonePort).Key;
        if (!nodePorts.ContainsKey(serverNode) || nodePorts[serverNode] != zonePort)
            return null;
        var clientNode = serverNode == 0 ? 1 : 0;

        var serverFrames = ParseFrames(nodeData[serverNode].ToArray());
        var handshake = serverFrames
            .Select(x => FiestaPacket.FromPayload(x))
            .FirstOrDefault(x => x.Header == 2 && x.Type == 7 && x.Body.Length >= 2);

        if (handshake.Body is null || handshake.Body.Length < 2)
            return null;

        var xorPosition = handshake.Body[0] | (handshake.Body[1] << 8);
        if (xorPosition is < 0 or >= FiestaXorCipher.TableLength)
            return null;

        var cipher = new FiestaXorCipher(xorPosition);
        foreach (var encryptedPayload in ParseFrames(nodeData[clientNode].ToArray()))
        {
            var decrypted = encryptedPayload.ToArray();
            cipher.TransformInPlace(decrypted);

            FiestaPacket packet;
            try { packet = FiestaPacket.FromPayload(decrypted); }
            catch { continue; }

            if (packet.Header != 6 || packet.Type != 1 || decrypted.Length < 20)
                continue;

            var randomId = (ushort)(decrypted[2] | (decrypted[3] << 8));
            var characterName = FiestaHeadlessLoadClient.ReadFixedAscii(decrypted.AsSpan(4, 16));
            if (string.IsNullOrWhiteSpace(characterName))
                continue;
            if (!string.IsNullOrWhiteSpace(expectedCharacterName)
                && !string.Equals(characterName, expectedCharacterName, StringComparison.Ordinal))
            {
                continue;
            }

            return new FiestaZoneTransferCandidate(
                streamId,
                xorPosition,
                randomId,
                characterName,
                decrypted);
        }

        return null;
    }

    private static IReadOnlyList<int> DiscoverCandidateStreams(string tshark, string capturePath, int zonePort)
    {
        var output = RunTshark(
            tshark,
            "-r", capturePath,
            "-Y", $"tcp.port == {zonePort}",
            "-T", "fields",
            "-e", "tcp.stream");

        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(x => int.Parse(x, CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
    }

    private static IReadOnlyList<byte[]> ParseFrames(byte[] stream)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset < stream.Length)
        {
            int headerLength;
            int payloadLength;
            if (stream[offset] == 0)
            {
                if (offset + 3 > stream.Length)
                    throw new InvalidDataException("Unvollständiger 3-Byte-Fiesta-Length-Header im TCP-Stream.");
                payloadLength = stream[offset + 1] | (stream[offset + 2] << 8);
                headerLength = 3;
            }
            else
            {
                payloadLength = stream[offset];
                headerLength = 1;
            }

            if (payloadLength <= 0)
                throw new InvalidDataException("Fiesta-Frame mit Länge 0 im TCP-Stream.");
            if (offset + headerLength + payloadLength > stream.Length)
                throw new InvalidDataException("TCP-Follow-Stream endet mitten in einem Fiesta-Frame.");

            var payload = new byte[payloadLength];
            Buffer.BlockCopy(stream, offset + headerLength, payload, 0, payloadLength);
            frames.Add(payload);
            offset += headerLength + payloadLength;
        }

        return frames;
    }

    private static string ResolveTshark(string? requestedPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestedPath))
            candidates.Add(requestedPath);

        candidates.Add("tshark.exe");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "Wireshark", "tshark.exe"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (candidate.Equals("tshark.exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var version = RunProcess(candidate, "--version");
                    if (version.Contains("TShark", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch { }
                continue;
            }

            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            "tshark.exe wurde nicht gefunden. Wireshark installieren oder TsharkPath explizit angeben.");
    }

    private static string RunTshark(string tshark, params string[] arguments)
        => RunProcess(tshark, arguments);

    private static string RunProcess(string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Prozess '{fileName}' konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"'{fileName}' lief länger als 30 Sekunden.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'{fileName}' beendete sich mit ExitCode {process.ExitCode}: {stderr.Trim()}");

        return stdout;
    }
}

public sealed class FiestaZoneTransferCaptureOptions
{
    public string CapturePath { get; init; } = string.Empty;
    public string OutputTemplatePath { get; init; } = string.Empty;
    public int ZonePort { get; init; } = 9016;
    public string? ExpectedCharacterName { get; init; }
    public string? TsharkPath { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CapturePath) || !File.Exists(CapturePath))
            throw new FileNotFoundException("PCAP/PCAPNG-Mitschnitt wurde nicht gefunden.", CapturePath);
        if (string.IsNullOrWhiteSpace(OutputTemplatePath))
            throw new ArgumentException("OutputTemplatePath fehlt.");
        if (ZonePort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(ZonePort));
        if (!string.IsNullOrWhiteSpace(ExpectedCharacterName) && ExpectedCharacterName.Length > 16)
            throw new ArgumentException("ExpectedCharacterName darf maximal 16 Zeichen lang sein.");
    }
}

public sealed class FiestaZoneTransferCaptureResult
{
    public bool Success { get; init; }
    public string TemplatePath { get; init; } = string.Empty;
    public int TcpStreamId { get; init; } = -1;
    public int XorPosition { get; init; }
    public ushort RandomId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public int PayloadLength { get; init; }
    public string PayloadSha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    public static FiestaZoneTransferCaptureResult Blocked(string detail)
        => new() { Success = false, Detail = "ZONE TRANSFER CAPTURE: BLOCKED · " + detail };
}

public readonly record struct FiestaZoneTransferCaptureSelfTestResult(bool Success, string Detail);

internal sealed record FiestaZoneTransferCandidate(
    int TcpStreamId,
    int XorPosition,
    ushort RandomId,
    string CharacterName,
    byte[] DecryptedPayload);
