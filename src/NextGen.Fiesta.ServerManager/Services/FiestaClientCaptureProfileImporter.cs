using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Extracts the non-secret NA2016 Login protocol profile from one real client capture.
/// Password/account data is neither parsed nor persisted.
/// </summary>
public sealed class FiestaClientCaptureProfileImporter
{
    private static readonly Regex NodeRegex = new(
        @"^Node\s+(?<node>[01]):\s+(?<endpoint>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HexRegex = new(
        @"^[0-9a-fA-F]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FiestaClientCaptureProfileResult Import(FiestaClientCaptureProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var tshark = ResolveTshark(options.TsharkPath);
        var streams = DiscoverCandidateStreams(tshark, options.CapturePath, options.LoginPort);
        if (streams.Count == 0)
        {
            return FiestaClientCaptureProfileResult.CreateBlocked(
                $"Im Mitschnitt wurde kein TCP-Stream mit Login-Port {options.LoginPort} gefunden.");
        }

        var candidates = new List<FiestaCapturedClientProfile>();
        foreach (var streamId in streams)
        {
            var follow = RunProcess(
                tshark,
                "-r", options.CapturePath,
                "-q",
                "-z", $"follow,tcp,raw,{streamId}");

            var profile = TryExtractFromFollowText(follow, streamId, options.LoginPort);
            if (profile is not null)
                candidates.Add(profile);
        }

        if (candidates.Count == 0)
        {
            return FiestaClientCaptureProfileResult.CreateBlocked(
                $"Keiner der {streams.Count} Login-TCP-Streams enthielt CH3/101 + CH3/11 unter einem gültigen SH2/7-XOR-Handshake.");
        }

        // A clean one-login capture should have exactly one complete login profile.
        // If several are present, they must agree byte-semantically on the protocol settings.
        var distinct = candidates
            .GroupBy(
                x => new
                {
                    Host = x.LoginHost.ToUpperInvariant(),
                    x.LoginPort,
                    x.ClientYear,
                    x.ClientVersion,
                    FileHash = x.FileHash ?? string.Empty,
                    x.WorldId,
                    WorldHost = x.WorldHost.ToUpperInvariant(),
                    x.WorldPort
                })
            .Select(g => g.First())
            .ToList();

        if (distinct.Count != 1)
        {
            return FiestaClientCaptureProfileResult.CreateBlocked(
                $"Der Mitschnitt enthält {distinct.Count} unterschiedliche Login-Protokollprofile. " +
                "Bitte einen sauberen Mitschnitt mit genau einem Clientlogin verwenden.");
        }

        var selected = distinct[0];

        var worldCandidates = DiscoverCandidateStreams(tshark, options.CapturePath, selected.WorldPort)
            .Select(streamId =>
            {
                var follow = RunProcess(
                    tshark,
                    "-r", options.CapturePath,
                    "-q",
                    "-z", $"follow,tcp,raw,{streamId}");
                return TryExtractWorldClientKeyFromFollowText(
                    follow,
                    streamId,
                    selected.WorldPort,
                    selected.WorldTransferKey);
            })
            .Where(x => x is not null)
            .Cast<FiestaWorldClientKeyCapture>()
            .ToList();

        if (worldCandidates.Count != 1)
        {
            return FiestaClientCaptureProfileResult.CreateBlocked(
                worldCandidates.Count == 0
                    ? $"Login-Profil wurde gefunden, aber kein passender CH3/15 WorldClientKey auf Port {selected.WorldPort}. " +
                      "Der Mitschnitt muss vom Login bis mindestens zur Charakterliste reichen."
                    : $"Es wurden {worldCandidates.Count} passende CH3/15 WorldClientKey-Streams gefunden. " +
                      "Bitte einen sauberen Mitschnitt mit genau einem Clientlogin verwenden.");
        }

        var worldClientKey = worldCandidates[0];
        selected = selected with
        {
            SourceCaptureSha256 = ComputeSha256(options.CapturePath),
            WorldClientKeyBodyBase64 = Convert.ToBase64String(worldClientKey.Body),
            WorldTcpStreamId = worldClientKey.TcpStreamId,
            WorldXorPosition = worldClientKey.XorPosition
        };
        selected.Validate();
        if (!selected.HasCapturedWorldClientKey)
            return FiestaClientCaptureProfileResult.CreateBlocked("CH3/15 WorldClientKey wurde nicht vollständig capture-basiert gesichert.");

        var output = Path.GetFullPath(options.OutputProfilePath);
        var directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(selected, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json + Environment.NewLine, new UTF8Encoding(false));

        return new FiestaClientCaptureProfileResult
        {
            Success = true,
            ProfilePath = output,
            Profile = selected,
            Detail =
                $"CLIENT CAPTURE PROFILE: SUCCESS · Login {selected.LoginHost}:{selected.LoginPort} · " +
                $"Client {selected.ClientYear}/{selected.ClientVersion} · World {selected.WorldId} → {selected.WorldHost}:{selected.WorldPort} · " +
                $"FileHash {(string.IsNullOrWhiteSpace(selected.FileHash) ? "nicht gesendet" : "capture-derived")} · " +
                $"tcp.stream={selected.TcpStreamId}."
        };
    }

    public static FiestaClientCaptureProfileSelfTestResult RunSelfTest()
    {
        try
        {
            const ushort xorPosition = 321;
            const ushort year = 2016;
            const ushort version = 77;
            const byte worldId = 3;
            const string fileHash = "0123456789abcdef0123456789abcdef";
            const string transferKey = "00112233445566778899aabbccddeeff";
            const ushort worldXorPosition = 211;

            var handshakePayload = BuildPayload(2, 7, new byte[]
            {
                (byte)(xorPosition & 0xff),
                (byte)(xorPosition >> 8)
            });

            var versionBody = new byte[4];
            versionBody[0] = (byte)(year & 0xff);
            versionBody[1] = (byte)(year >> 8);
            versionBody[2] = (byte)(version & 0xff);
            versionBody[3] = (byte)(version >> 8);

            const string worldHost = "127.0.0.1";
            const ushort worldPort = 9013;

            var worldRedirectBody = new byte[83];
            worldRedirectBody[0] = 1;
            FiestaHeadlessLoadClient.WriteFixedAscii(worldRedirectBody.AsSpan(1, 16), worldHost);
            worldRedirectBody[17] = (byte)(worldPort & 0xff);
            worldRedirectBody[18] = (byte)(worldPort >> 8);
            FiestaHeadlessLoadClient.WriteFixedAscii(worldRedirectBody.AsSpan(19, 32), transferKey);
            var worldRedirectPayload = BuildPayload(3, 12, worldRedirectBody);

            var fileHashBytes = Encoding.ASCII.GetBytes(fileHash + "\0");
            var clientPayloads = new[]
            {
                BuildPayload(3, 101, versionBody),
                BuildPayload(3, 4, fileHashBytes),
                BuildPayload(3, 11, new[] { worldId })
            };

            var cipher = new FiestaXorCipher(xorPosition);
            var clientStream = new MemoryStream();
            foreach (var payload in clientPayloads)
            {
                var encrypted = payload.ToArray();
                cipher.TransformInPlace(encrypted);
                var frame = FiestaWireConnection.FramePayload(encrypted);
                clientStream.Write(frame);
            }

            var serverStream = new MemoryStream();
            serverStream.Write(FiestaWireConnection.FramePayload(handshakePayload));
            serverStream.Write(FiestaWireConnection.FramePayload(worldRedirectPayload));
            var follow =
                "===================================================================\n" +
                "Follow: tcp,raw\n" +
                "Filter: tcp.stream eq 9\n" +
                "Node 0: 127.0.0.1:55001\n" +
                "Node 1: 127.0.0.1:9010\n" +
                Convert.ToHexString(clientStream.ToArray()).ToLowerInvariant() + "\n" +
                "\t" + Convert.ToHexString(serverStream.ToArray()).ToLowerInvariant() + "\n" +
                "===================================================================\n";

            var profile = TryExtractFromFollowText(follow, 9, 9010)
                          ?? throw new InvalidDataException("Synthetisches Login-Profil wurde nicht extrahiert.");

            if (profile.LoginHost != "127.0.0.1"
                || profile.LoginPort != 9010
                || profile.ClientYear != year
                || profile.ClientVersion != version
                || profile.WorldId != worldId
                || profile.WorldHost != worldHost
                || profile.WorldPort != worldPort
                || profile.FileHash != fileHash
                || profile.XorPosition != xorPosition
                || profile.WorldTransferKey != transferKey)
            {
                throw new InvalidDataException(
                    "Capture-Profil veränderte Login-/World-Endpoint, Version, FileHash, World-ID, Transfer-Key oder XOR-Position.");
            }

            var worldHandshakePayload = BuildPayload(2, 7, new byte[]
            {
                (byte)(worldXorPosition & 0xff),
                (byte)(worldXorPosition >> 8)
            });
            var capturedWorldBody = new byte[50];
            for (var i = 0; i < 18; i++)
                capturedWorldBody[i] = unchecked((byte)(0xA0 + i));
            FiestaHeadlessLoadClient.WriteFixedAscii(capturedWorldBody.AsSpan(18, 32), transferKey);

            var worldClientPayload = BuildPayload(3, 15, capturedWorldBody);
            var encryptedWorld = worldClientPayload.ToArray();
            var worldCipher = new FiestaXorCipher(worldXorPosition);
            worldCipher.TransformInPlace(encryptedWorld);

            var worldFollow =
                "===================================================================\n" +
                "Follow: tcp,raw\n" +
                "Filter: tcp.stream eq 10\n" +
                "Node 0: 127.0.0.1:55002\n" +
                $"Node 1: 127.0.0.1:{worldPort}\n" +
                Convert.ToHexString(FiestaWireConnection.FramePayload(encryptedWorld)).ToLowerInvariant() + "\n" +
                "\t" + Convert.ToHexString(FiestaWireConnection.FramePayload(worldHandshakePayload)).ToLowerInvariant() + "\n" +
                "===================================================================\n";

            var capturedWorld = TryExtractWorldClientKeyFromFollowText(
                                    worldFollow,
                                    10,
                                    worldPort,
                                    transferKey)
                                ?? throw new InvalidDataException("Synthetischer CH3/15 WorldClientKey wurde nicht extrahiert.");

            var enriched = profile with
            {
                WorldClientKeyBodyBase64 = Convert.ToBase64String(capturedWorld.Body),
                WorldTcpStreamId = capturedWorld.TcpStreamId,
                WorldXorPosition = capturedWorld.XorPosition
            };
            enriched.Validate();
            if (!enriched.HasCapturedWorldClientKey)
                throw new InvalidDataException("Capture-Profil markiert den vorhandenen CH3/15-Body nicht als vollständig.");

            const string replacementKey = "ffeeddccbbaa99887766554433221100";
            var materialized = enriched.MaterializeWorldClientKeyBody(replacementKey);
            if (!materialized.AsSpan(0, 18).SequenceEqual(capturedWorldBody.AsSpan(0, 18))
                || FiestaHeadlessLoadClient.ReadFixedAscii(materialized.AsSpan(18, 32)) != replacementKey)
            {
                throw new InvalidDataException("WorldClientKey-Materialisierung veränderte Capture-Prefix oder ersetzte Transfer-Key nicht korrekt.");
            }

            return new FiestaClientCaptureProfileSelfTestResult(
                true,
                "CLIENT CAPTURE PROFILE SELFTEST: PASS · Login-Profil + echter CH3/15-Body capture-basiert, XOR kontinuierlich.");
        }
        catch (Exception ex)
        {
            return new FiestaClientCaptureProfileSelfTestResult(
                false,
                "CLIENT CAPTURE PROFILE SELFTEST: FAIL · " + ex.Message);
        }
    }

    internal static FiestaCapturedClientProfile? TryExtractFromFollowText(
        string followText,
        int streamId,
        int loginPort)
    {
        if (string.IsNullOrWhiteSpace(followText))
            return null;

        var parsed = ParseFollow(followText);
        var serverNode = parsed.Endpoints
            .Where(x => x.Value.Port == loginPort)
            .Select(x => (int?)x.Key)
            .FirstOrDefault();
        if (!serverNode.HasValue)
            return null;

        var clientNode = serverNode.Value == 0 ? 1 : 0;
        if (!parsed.NodeData.TryGetValue(serverNode.Value, out var serverData)
            || !parsed.NodeData.TryGetValue(clientNode, out var clientData))
        {
            return null;
        }

        var serverFrames = ParseFrames(serverData);
        FiestaPacket? handshake = null;
        string? worldHost = null;
        int? worldPort = null;
        string? worldTransferKey = null;
        foreach (var frame in serverFrames)
        {
            var packet = FiestaPacket.FromPayload(frame);
            if (packet.Header == 2 && packet.Type == 7 && packet.Body.Length >= 2)
            {
                handshake = packet;
                continue;
            }

            if (packet.Header == 3 && packet.Type == 12 && packet.Body.Length >= 51)
            {
                worldHost = FiestaHeadlessLoadClient.ReadFixedAscii(packet.Body.AsSpan(1, 16));
                worldPort = packet.Body[17] | (packet.Body[18] << 8);
                worldTransferKey = FiestaHeadlessLoadClient.ReadFixedAscii(packet.Body.AsSpan(19, 32));
            }
        }

        if (!handshake.HasValue
            || string.IsNullOrWhiteSpace(worldHost)
            || !worldPort.HasValue
            || string.IsNullOrWhiteSpace(worldTransferKey))
            return null;

        var xorPosition = handshake.Value.Body[0] | (handshake.Value.Body[1] << 8);
        if (xorPosition is < 0 or >= FiestaXorCipher.TableLength)
            return null;

        ushort? clientYear = null;
        ushort? clientVersion = null;
        string? fileHash = null;
        byte? worldId = null;

        var cipher = new FiestaXorCipher(xorPosition);
        foreach (var encryptedPayload in ParseFrames(clientData))
        {
            var decrypted = encryptedPayload.ToArray();
            cipher.TransformInPlace(decrypted);

            FiestaPacket packet;
            try { packet = FiestaPacket.FromPayload(decrypted); }
            catch { continue; }

            if (packet.Header != 3)
                continue;

            switch (packet.Type)
            {
                case 101 when packet.Body.Length >= 4:
                    clientYear = (ushort)(packet.Body[0] | (packet.Body[1] << 8));
                    clientVersion = (ushort)(packet.Body[2] | (packet.Body[3] << 8));
                    break;

                case 4:
                    fileHash = FiestaHeadlessLoadClient.ReadFixedAscii(packet.Body);
                    if (string.IsNullOrWhiteSpace(fileHash))
                        fileHash = null;
                    break;

                case 11 when packet.Body.Length >= 1:
                    worldId = packet.Body[0];
                    break;
            }
        }

        if (!clientYear.HasValue || !clientVersion.HasValue || !worldId.HasValue)
            return null;

        var endpoint = parsed.Endpoints[serverNode.Value];
        var profile = new FiestaCapturedClientProfile
        {
            LoginHost = endpoint.Host,
            LoginPort = endpoint.Port,
            ClientYear = clientYear.Value,
            ClientVersion = clientVersion.Value,
            FileHash = fileHash,
            WorldId = worldId.Value,
            WorldHost = worldHost,
            WorldPort = worldPort.Value,
            TcpStreamId = streamId,
            XorPosition = xorPosition,
            WorldTransferKey = worldTransferKey
        };
        profile.Validate();
        return profile;
    }

    internal static FiestaWorldClientKeyCapture? TryExtractWorldClientKeyFromFollowText(
        string followText,
        int streamId,
        int worldPort,
        string expectedTransferKey)
    {
        if (string.IsNullOrWhiteSpace(followText) || string.IsNullOrWhiteSpace(expectedTransferKey))
            return null;

        var parsed = ParseFollow(followText);
        var serverNode = parsed.Endpoints
            .Where(x => x.Value.Port == worldPort)
            .Select(x => (int?)x.Key)
            .FirstOrDefault();
        if (!serverNode.HasValue)
            return null;

        var clientNode = serverNode.Value == 0 ? 1 : 0;
        if (!parsed.NodeData.TryGetValue(serverNode.Value, out var serverData)
            || !parsed.NodeData.TryGetValue(clientNode, out var clientData))
        {
            return null;
        }

        FiestaPacket? handshake = null;
        foreach (var frame in ParseFrames(serverData))
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
        foreach (var encryptedPayload in ParseFrames(clientData))
        {
            var decrypted = encryptedPayload.ToArray();
            cipher.TransformInPlace(decrypted);

            FiestaPacket packet;
            try { packet = FiestaPacket.FromPayload(decrypted); }
            catch { continue; }

            if (packet.Header != 3 || packet.Type != 15 || packet.Body.Length < 50)
                continue;

            var transferKey = FiestaHeadlessLoadClient.ReadFixedAscii(packet.Body.AsSpan(18, 32));
            if (!string.Equals(transferKey, expectedTransferKey, StringComparison.Ordinal))
                continue;

            return new FiestaWorldClientKeyCapture(
                streamId,
                xorPosition,
                packet.Body.ToArray());
        }

        return null;
    }

    private static FollowStream ParseFollow(string text)
    {
        var endpoints = new Dictionary<int, FiestaCaptureEndpoint>();
        var data = new Dictionary<int, MemoryStream>
        {
            [0] = new MemoryStream(),
            [1] = new MemoryStream()
        };

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var nodeMatch = NodeRegex.Match(line.Trim());
            if (nodeMatch.Success)
            {
                var node = int.Parse(nodeMatch.Groups["node"].Value, CultureInfo.InvariantCulture);
                if (TryParseEndpoint(nodeMatch.Groups["endpoint"].Value.Trim(), out var endpoint))
                    endpoints[node] = endpoint;
                continue;
            }

            var nodeIndex = line.StartsWith('\t') ? 1 : 0;
            var candidate = line.Trim();
            if (candidate.Length == 0 || candidate.Length % 2 != 0 || !HexRegex.IsMatch(candidate))
                continue;

            var bytes = Convert.FromHexString(candidate);
            data[nodeIndex].Write(bytes);
        }

        return new FollowStream(
            endpoints,
            data.ToDictionary(x => x.Key, x => x.Value.ToArray()));
    }

    private static bool TryParseEndpoint(string text, out FiestaCaptureEndpoint endpoint)
    {
        endpoint = default;
        var lastColon = text.LastIndexOf(':');
        if (lastColon <= 0
            || !int.TryParse(
                text[(lastColon + 1)..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var port))
        {
            return false;
        }

        var host = text[..lastColon].Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
            return false;

        endpoint = new FiestaCaptureEndpoint(host, port);
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

    private static byte[] BuildPayload(int header, int type, ReadOnlySpan<byte> body)
    {
        var payload = new byte[2 + body.Length];
        var opcode = FiestaHeadlessLoadClient.PackOpcode(header, type);
        payload[0] = (byte)(opcode & 0xff);
        payload[1] = (byte)(opcode >> 8);
        body.CopyTo(payload.AsSpan(2));
        return payload;
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
        {
            throw new InvalidOperationException(
                $"'{fileName}' beendete sich mit ExitCode {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private readonly record struct FiestaCaptureEndpoint(string Host, int Port);
    private sealed record FollowStream(
        IReadOnlyDictionary<int, FiestaCaptureEndpoint> Endpoints,
        IReadOnlyDictionary<int, byte[]> NodeData);
}

public sealed class FiestaClientCaptureProfileOptions
{
    public string CapturePath { get; init; } = string.Empty;
    public string OutputProfilePath { get; init; } = string.Empty;
    public int LoginPort { get; init; } = 9010;
    public string? TsharkPath { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CapturePath) || !File.Exists(CapturePath))
            throw new FileNotFoundException("PCAP/PCAPNG-Mitschnitt wurde nicht gefunden.", CapturePath);
        if (string.IsNullOrWhiteSpace(OutputProfilePath))
            throw new ArgumentException("OutputProfilePath fehlt.");
        if (LoginPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(LoginPort));
    }
}

public sealed record FiestaCapturedClientProfile
{
    public const string FormatV1 = "NextGen.NA2016.ClientCaptureProfile.v1";

    public string Format { get; init; } = FormatV1;
    public string LoginHost { get; init; } = string.Empty;
    public int LoginPort { get; init; }
    public ushort ClientYear { get; init; }
    public ushort ClientVersion { get; init; }
    public string? FileHash { get; init; }
    public byte WorldId { get; init; }
    public string WorldHost { get; init; } = string.Empty;
    public int WorldPort { get; init; }
    public int TcpStreamId { get; init; } = -1;
    public int XorPosition { get; init; }
    public string WorldClientKeyBodyBase64 { get; init; } = string.Empty;
    public int WorldTcpStreamId { get; init; } = -1;
    public int WorldXorPosition { get; init; } = -1;
    public string SourceCaptureSha256 { get; init; } = string.Empty;

    [JsonIgnore]
    internal string WorldTransferKey { get; init; } = string.Empty;

    [JsonIgnore]
    public bool HasCapturedWorldClientKey
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorldClientKeyBodyBase64)
                || WorldTcpStreamId < 0
                || WorldXorPosition is < 0 or >= FiestaXorCipher.TableLength)
            {
                return false;
            }

            try
            {
                return Convert.FromBase64String(WorldClientKeyBodyBase64).Length >= 50;
            }
            catch
            {
                return false;
            }
        }
    }

    public static FiestaCapturedClientProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<FiestaCapturedClientProfile>(
                          File.ReadAllText(path, Encoding.UTF8))
                      ?? throw new InvalidDataException("Client-Capture-Profil ist leer oder ungültig.");
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (!string.Equals(Format, FormatV1, StringComparison.Ordinal))
            throw new InvalidDataException($"Unbekanntes Client-Capture-Profilformat '{Format}'.");
        if (string.IsNullOrWhiteSpace(LoginHost))
            throw new InvalidDataException("LoginHost fehlt im Capture-Profil.");
        if (LoginPort is <= 0 or > 65535)
            throw new InvalidDataException("LoginPort liegt außerhalb 1..65535.");
        if (ClientYear == 0 || ClientVersion == 0)
            throw new InvalidDataException("ClientYear/ClientVersion fehlen im Capture-Profil.");
        if (string.IsNullOrWhiteSpace(WorldHost) || WorldPort is <= 0 or > 65535)
            throw new InvalidDataException("WorldHost/WorldPort fehlen im Capture-Profil.");
        if (TcpStreamId < 0)
            throw new InvalidDataException("TcpStreamId fehlt im Capture-Profil.");
        if (XorPosition is < 0 or >= FiestaXorCipher.TableLength)
            throw new InvalidDataException("XOR-Position liegt außerhalb der verifizierten 499-Byte-Tabelle.");

        if (!string.IsNullOrWhiteSpace(WorldClientKeyBodyBase64))
        {
            byte[] body;
            try { body = Convert.FromBase64String(WorldClientKeyBodyBase64); }
            catch (FormatException ex) { throw new InvalidDataException("WorldClientKeyBodyBase64 ist ungültig.", ex); }
            if (body.Length < 50)
                throw new InvalidDataException($"CH3/15 WorldClientKey-Body ist zu kurz ({body.Length} Byte; mindestens 50 erwartet).");
            if (WorldTcpStreamId < 0)
                throw new InvalidDataException("WorldTcpStreamId fehlt für den capture-basierten CH3/15-Body.");
            if (WorldXorPosition is < 0 or >= FiestaXorCipher.TableLength)
                throw new InvalidDataException("WorldXorPosition liegt außerhalb der verifizierten 499-Byte-Tabelle.");
        }
    }

    public byte[] MaterializeWorldClientKeyBody(string transferKey)
    {
        Validate();
        if (!HasCapturedWorldClientKey)
            throw new InvalidDataException(
                "Capture-Profil enthält keinen vollständigen CH3/15 WorldClientKey-Body. Capture erneut vom Login bis zur Charakterliste importieren.");
        if (string.IsNullOrWhiteSpace(transferKey) || Encoding.ASCII.GetByteCount(transferKey) > 32)
            throw new ArgumentException("World-Transfer-Key fehlt oder ist länger als 32 ASCII-Byte.", nameof(transferKey));

        var body = Convert.FromBase64String(WorldClientKeyBodyBase64);
        FiestaHeadlessLoadClient.WriteFixedAscii(body.AsSpan(18, 32), transferKey);
        return body;
    }
}

public sealed class FiestaClientCaptureProfileResult
{
    public bool Success { get; init; }
    public string ProfilePath { get; init; } = string.Empty;
    public FiestaCapturedClientProfile? Profile { get; init; }
    public string Detail { get; init; } = string.Empty;

    public static FiestaClientCaptureProfileResult CreateBlocked(string detail)
        => new()
        {
            Success = false,
            Detail = "CLIENT CAPTURE PROFILE: BLOCKED · " + detail
        };
}

public readonly record struct FiestaClientCaptureProfileSelfTestResult(bool Success, string Detail);
