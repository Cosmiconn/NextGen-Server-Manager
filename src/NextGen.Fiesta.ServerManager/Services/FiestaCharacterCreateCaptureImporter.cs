using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Imports one real client -> World CH5/1 CharacterCreate packet.
/// The full packet is retained. Per synthetic account only slot and character name are replaced.
/// </summary>
public sealed class FiestaCharacterCreateCaptureImporter
{
    private static readonly Regex NodeRegex = new(
        @"^Node\s+(?<node>[01]):\s+(?<endpoint>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HexRegex = new(
        @"^[0-9a-fA-F]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FiestaCharacterCreateCaptureResult Import(FiestaCharacterCreateCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var tshark = ResolveTshark(options.TsharkPath);
        var streams = DiscoverCandidateStreams(tshark, options.CapturePath, options.WorldPort);
        if (streams.Count == 0)
        {
            return FiestaCharacterCreateCaptureResult.CreateBlocked(
                $"Im Mitschnitt wurde kein TCP-Stream mit World-Port {options.WorldPort} gefunden.");
        }

        var candidates = new List<FiestaCharacterCreateCandidate>();
        foreach (var streamId in streams)
        {
            var follow = RunProcess(
                tshark,
                "-r", options.CapturePath,
                "-q",
                "-z", $"follow,tcp,raw,{streamId}");

            var candidate = TryExtractFromFollowText(
                follow,
                streamId,
                options.WorldPort,
                options.ExpectedCharacterName);
            if (candidate is not null)
                candidates.Add(candidate);
        }

        if (candidates.Count != 1)
        {
            return FiestaCharacterCreateCaptureResult.CreateBlocked(
                candidates.Count == 0
                    ? $"Kein eindeutig entschlüsselbares CH5/1 CharacterCreate für '{options.ExpectedCharacterName}' gefunden. " +
                      "Der Mitschnitt muss die tatsächliche Charaktererstellung enthalten."
                    : $"Mehrere CH5/1 CharacterCreate-Pakete für '{options.ExpectedCharacterName}' gefunden; bitte einen Mitschnitt mit genau einer Erstellung verwenden.");
        }

        var selected = candidates[0];
        var template = new FiestaCharacterCreateTemplate
        {
            PayloadBase64 = Convert.ToBase64String(selected.DecryptedPayload),
            SlotOffset = 2,
            CharacterNameOffset = 3,
            CharacterNameLength = 20,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(selected.DecryptedPayload))
        };
        template.Validate();

        var output = Path.GetFullPath(options.OutputTemplatePath);
        var directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(
            output,
            JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));

        return new FiestaCharacterCreateCaptureResult
        {
            Success = true,
            TemplatePath = output,
            TcpStreamId = selected.TcpStreamId,
            XorPosition = selected.XorPosition,
            Slot = selected.Slot,
            CharacterName = selected.CharacterName,
            PayloadLength = selected.DecryptedPayload.Length,
            PayloadSha256 = template.SourceSha256 ?? string.Empty,
            Detail =
                $"CHARACTER CREATE CAPTURE: SUCCESS · tcp.stream={selected.TcpStreamId} · " +
                $"CH5/1 {selected.DecryptedPayload.Length} Byte · Slot {selected.Slot} · " +
                $"Charakter={selected.CharacterName} · XOR={selected.XorPosition}."
        };
    }

    public static FiestaCharacterCreateCaptureSelfTestResult RunSelfTest()
    {
        try
        {
            const ushort xorPosition = 222;
            const string character = "CreateProbe";
            const byte slot = 0;

            var handshake = BuildPayload(2, 7, new byte[]
            {
                (byte)(xorPosition & 0xff),
                (byte)(xorPosition >> 8)
            });

            // Real structure proven by original/emulator handler:
            // byte slot + char name[20] + jobGender + hair + color + style.
            var body = new byte[25];
            body[0] = slot;
            FiestaHeadlessLoadClient.WriteFixedAscii(body.AsSpan(1, 20), character);
            body[21] = 0x84; // preserved template data; not interpreted by the importer.
            body[22] = 2;
            body[23] = 3;
            body[24] = 4;
            var create = BuildPayload(5, 1, body);

            var encrypted = create.ToArray();
            new FiestaXorCipher(xorPosition).TransformInPlace(encrypted);

            var follow =
                "===================================================================\n" +
                "Follow: tcp,raw\n" +
                "Filter: tcp.stream eq 12\n" +
                "Node 0: 127.0.0.1:55100\n" +
                "Node 1: 127.0.0.1:9013\n" +
                Convert.ToHexString(FiestaWireConnection.FramePayload(encrypted)).ToLowerInvariant() + "\n" +
                "\t" + Convert.ToHexString(FiestaWireConnection.FramePayload(handshake)).ToLowerInvariant() + "\n" +
                "===================================================================\n";

            var parsed = TryExtractFromFollowText(follow, 12, 9013, character)
                         ?? throw new InvalidDataException("Synthetisches CH5/1 CharacterCreate wurde nicht extrahiert.");

            var template = new FiestaCharacterCreateTemplate
            {
                PayloadBase64 = Convert.ToBase64String(parsed.DecryptedPayload),
                SlotOffset = 2,
                CharacterNameOffset = 3,
                CharacterNameLength = 20,
                SourceSha256 = Convert.ToHexString(SHA256.HashData(parsed.DecryptedPayload))
            };
            var materialized = template.Materialize(4, "OtherProbe");

            if (parsed.Slot != slot
                || parsed.CharacterName != character
                || materialized[2] != 4
                || FiestaHeadlessLoadClient.ReadFixedAscii(materialized.AsSpan(3, 20)) != "OtherProbe"
                || !materialized.AsSpan(23).SequenceEqual(create.AsSpan(23)))
            {
                throw new InvalidDataException("CharacterCreate-Template veränderte mehr als Slot/Name oder materialisierte die Felder falsch.");
            }

            return new FiestaCharacterCreateCaptureSelfTestResult(
                true,
                "CHARACTER CREATE CAPTURE SELFTEST: PASS · CH5/1 XOR, Slot/Name-Patch und statische Templatebytes bestätigt.");
        }
        catch (Exception ex)
        {
            return new FiestaCharacterCreateCaptureSelfTestResult(
                false,
                "CHARACTER CREATE CAPTURE SELFTEST: FAIL · " + ex.Message);
        }
    }

    internal static FiestaCharacterCreateCandidate? TryExtractFromFollowText(
        string followText,
        int streamId,
        int worldPort,
        string expectedCharacterName)
    {
        var parsed = ParseFollow(followText);
        var serverNode = parsed.Endpoints
            .Where(x => x.Value.Port == worldPort)
            .Select(x => (int?)x.Key)
            .FirstOrDefault();
        if (!serverNode.HasValue)
            return null;

        var clientNode = serverNode.Value == 0 ? 1 : 0;
        var serverFrames = ParseFrames(parsed.NodeData[serverNode.Value]);
        FiestaPacket? handshake = null;
        foreach (var frame in serverFrames)
        {
            var packet = FiestaPacket.FromPayload(frame);
            if (packet.Header == 2 && packet.Type == 7 && packet.Body.Length >= 2)
            {
                handshake = packet;
                break;
            }
        }

        if (!handshake.HasValue)
            return null;

        var xorPosition = handshake.Value.Body[0] | (handshake.Value.Body[1] << 8);
        if (xorPosition is < 0 or >= FiestaXorCipher.TableLength)
            return null;

        var cipher = new FiestaXorCipher(xorPosition);
        foreach (var encryptedPayload in ParseFrames(parsed.NodeData[clientNode]))
        {
            var decrypted = encryptedPayload.ToArray();
            cipher.TransformInPlace(decrypted);

            FiestaPacket packet;
            try { packet = FiestaPacket.FromPayload(decrypted); }
            catch { continue; }

            if (packet.Header != 5 || packet.Type != 1 || decrypted.Length < 27)
                continue;

            var slot = decrypted[2];
            var characterName = FiestaHeadlessLoadClient.ReadFixedAscii(decrypted.AsSpan(3, 20));
            if (!string.Equals(characterName, expectedCharacterName, StringComparison.Ordinal))
                continue;

            return new FiestaCharacterCreateCandidate(
                streamId,
                xorPosition,
                slot,
                characterName,
                decrypted);
        }

        return null;
    }

    private static FollowStream ParseFollow(string text)
    {
        var endpoints = new Dictionary<int, CaptureEndpoint>();
        var streams = new Dictionary<int, MemoryStream>
        {
            [0] = new MemoryStream(),
            [1] = new MemoryStream()
        };

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var node = NodeRegex.Match(line.Trim());
            if (node.Success)
            {
                var nodeIndex = int.Parse(node.Groups["node"].Value, CultureInfo.InvariantCulture);
                if (TryParseEndpoint(node.Groups["endpoint"].Value.Trim(), out var endpoint))
                    endpoints[nodeIndex] = endpoint;
                continue;
            }

            var dataNode = line.StartsWith('\t') ? 1 : 0;
            var hex = line.Trim();
            if (hex.Length == 0 || hex.Length % 2 != 0 || !HexRegex.IsMatch(hex))
                continue;
            streams[dataNode].Write(Convert.FromHexString(hex));
        }

        return new FollowStream(
            endpoints,
            streams.ToDictionary(x => x.Key, x => x.Value.ToArray()));
    }

    private static bool TryParseEndpoint(string text, out CaptureEndpoint endpoint)
    {
        endpoint = default;
        var colon = text.LastIndexOf(':');
        if (colon <= 0
            || !int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            return false;

        var host = text[..colon].Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
            return false;

        endpoint = new CaptureEndpoint(host, port);
        return true;
    }

    private static IReadOnlyList<int> DiscoverCandidateStreams(string tshark, string capturePath, int port)
    {
        var output = RunProcess(
            tshark,
            "-r", capturePath,
            "-Y", $"tcp.port == {port}",
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
            int length;
            if (stream[offset] == 0)
            {
                if (offset + 3 > stream.Length)
                    throw new InvalidDataException("Unvollständiger großer Fiesta-Frameheader.");
                length = stream[offset + 1] | (stream[offset + 2] << 8);
                headerLength = 3;
            }
            else
            {
                length = stream[offset];
                headerLength = 1;
            }

            if (length <= 0 || offset + headerLength + length > stream.Length)
                throw new InvalidDataException("Ungültiger/unvollständiger Fiesta-Frame im World-TCP-Stream.");

            var payload = new byte[length];
            Buffer.BlockCopy(stream, offset + headerLength, payload, 0, length);
            frames.Add(payload);
            offset += headerLength + length;
        }

        return frames;
    }

    private static byte[] BuildPayload(int header, int type, ReadOnlySpan<byte> body)
    {
        var payload = new byte[2 + body.Length];
        var opcode = FiestaHeadlessLoadClient.PackOpcode(header, type);
        payload[0] = (byte)(opcode & 0xff);
        payload[1] = (byte)(opcode >> 8);
        body.CopyTo(payload.AsSpan(2));
        return payload;
    }

    private static string ResolveTshark(string? requested)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested))
            candidates.Add(requested);
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
                    if (RunProcess(candidate, "--version").Contains("TShark", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch { }
            }
            else if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("tshark.exe wurde nicht gefunden.");
    }

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
                            ?? throw new InvalidOperationException($"'{fileName}' konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"'{fileName}' lief länger als 30 Sekunden.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{fileName}' ExitCode {process.ExitCode}: {stderr.Trim()}");
        return stdout;
    }

    private readonly record struct CaptureEndpoint(string Host, int Port);
    private sealed record FollowStream(
        IReadOnlyDictionary<int, CaptureEndpoint> Endpoints,
        IReadOnlyDictionary<int, byte[]> NodeData);
}

public sealed class FiestaCharacterCreateCaptureOptions
{
    public string CapturePath { get; init; } = string.Empty;
    public string OutputTemplatePath { get; init; } = string.Empty;
    public int WorldPort { get; init; }
    public string ExpectedCharacterName { get; init; } = string.Empty;
    public string? TsharkPath { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CapturePath) || !File.Exists(CapturePath))
            throw new FileNotFoundException("PCAP/PCAPNG-Mitschnitt wurde nicht gefunden.", CapturePath);
        if (string.IsNullOrWhiteSpace(OutputTemplatePath))
            throw new ArgumentException("OutputTemplatePath fehlt.");
        if (WorldPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(WorldPort));
        if (string.IsNullOrWhiteSpace(ExpectedCharacterName) || ExpectedCharacterName.Length > 20)
            throw new ArgumentException("ExpectedCharacterName fehlt oder ist länger als 20 Zeichen.");
    }
}

public sealed class FiestaCharacterCreateCaptureResult
{
    public bool Success { get; init; }
    public string TemplatePath { get; init; } = string.Empty;
    public int TcpStreamId { get; init; } = -1;
    public int XorPosition { get; init; }
    public byte Slot { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public int PayloadLength { get; init; }
    public string PayloadSha256 { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    public static FiestaCharacterCreateCaptureResult CreateBlocked(string detail)
        => new() { Detail = "CHARACTER CREATE CAPTURE: BLOCKED · " + detail };
}

public sealed class FiestaCharacterCreateTemplate
{
    public const string FormatV1 = "NextGen.NA2016.CharacterCreateTemplate.v1";

    public string Format { get; init; } = FormatV1;
    public string PayloadBase64 { get; init; } = string.Empty;
    public int SlotOffset { get; init; } = 2;
    public int CharacterNameOffset { get; init; } = 3;
    public int CharacterNameLength { get; init; } = 20;
    public string? SourceSha256 { get; init; }

    public static FiestaCharacterCreateTemplate Load(string path)
    {
        var template = JsonSerializer.Deserialize<FiestaCharacterCreateTemplate>(
                           File.ReadAllText(path, Encoding.UTF8))
                       ?? throw new InvalidDataException("CharacterCreate-Template ist leer oder ungültig.");
        template.Validate();
        return template;
    }

    public byte[] Materialize(byte slot, string characterName)
    {
        Validate();
        if (string.IsNullOrWhiteSpace(characterName) || characterName.Length > CharacterNameLength)
            throw new ArgumentException($"Charaktername muss 1..{CharacterNameLength} Zeichen lang sein.");

        var payload = Convert.FromBase64String(PayloadBase64);
        payload[SlotOffset] = slot;
        FiestaHeadlessLoadClient.WriteFixedAscii(
            payload.AsSpan(CharacterNameOffset, CharacterNameLength),
            characterName);

        var packet = FiestaPacket.FromPayload(payload);
        if (packet.Header != 5 || packet.Type != 1)
            throw new InvalidDataException("Materialisiertes CharacterCreate-Template ist nicht CH5/1.");
        return payload;
    }

    public void Validate()
    {
        if (!string.Equals(Format, FormatV1, StringComparison.Ordinal))
            throw new InvalidDataException($"Unbekanntes CharacterCreate-Templateformat '{Format}'.");

        byte[] payload;
        try { payload = Convert.FromBase64String(PayloadBase64); }
        catch (FormatException ex) { throw new InvalidDataException("PayloadBase64 ist ungültig.", ex); }

        var packet = FiestaPacket.FromPayload(payload);
        if (packet.Header != 5 || packet.Type != 1)
            throw new InvalidDataException("CharacterCreate-Template enthält nicht CH5/1.");
        if (SlotOffset < 2 || SlotOffset >= payload.Length)
            throw new InvalidDataException("SlotOffset liegt außerhalb des Templates.");
        if (CharacterNameOffset < 2
            || CharacterNameLength <= 0
            || CharacterNameOffset + CharacterNameLength > payload.Length)
            throw new InvalidDataException("CharacterNameOffset/Length liegt außerhalb des Templates.");

        if (!string.IsNullOrWhiteSpace(SourceSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(payload));
            if (!actual.Equals(SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CharacterCreate-Template stimmt nicht mit SourceSha256 überein.");
        }
    }
}

public readonly record struct FiestaCharacterCreateCaptureSelfTestResult(bool Success, string Detail);

internal sealed record FiestaCharacterCreateCandidate(
    int TcpStreamId,
    int XorPosition,
    byte Slot,
    string CharacterName,
    byte[] DecryptedPayload);
