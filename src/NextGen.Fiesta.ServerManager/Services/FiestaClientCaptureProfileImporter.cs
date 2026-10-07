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
                    VersionBody = x.ClientVersionBodyBase64,
                    FileHash = x.FileHash ?? string.Empty,
                    FileHashBody = x.FileHashBodyBase64,
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
            WorldClientKeyTransferKeyOffset = worldClientKey.TransferKeyOffset,
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
                $"CH3/101 {Convert.FromBase64String(selected.ClientVersionBodyBase64).Length} Byte capture-derived · " +
                $"World {selected.WorldId} → {selected.WorldHost}:{selected.WorldPort} · " +
                $"FileHash {(string.IsNullOrWhiteSpace(selected.FileHash) ? "nicht gesendet" : "capture-derived")} · " +
                $"CH3/15 KeyOffset {selected.WorldClientKeyTransferKeyOffset} · tcp.stream={selected.TcpStreamId}."
        };
    }

    public static FiestaClientCaptureProfileSelfTestResult RunSelfTest()
    {
        try
        {
            const ushort xorPosition = 321;
            const byte worldId = 3;
            const string fileHash = "33B543B0CA6E7C41E5D1D0651307";
            const ushort worldXorPosition = 211;
            const int worldKeyOffset = 256;

            var transferKey = Enumerable.Range(0, 32)
                .Select(i => unchecked((byte)(0x80 + i)))
                .ToArray();

            var handshakePayload = BuildPayload(2, 7, new byte[]
            {
                (byte)(xorPosition & 0xff),
                (byte)(xorPosition >> 8)
            });

            var versionBody = new byte[64];
            Encoding.ASCII.GetBytes("10022024000000").CopyTo(versionBody, 0);
            for (var i = 16; i < versionBody.Length; i++)
                versionBody[i] = unchecked((byte)(i * 17 + 3));

            const string worldHost = "127.0.0.1";
            const ushort worldPort = 9013;

            var worldRedirectBody = new byte[83];
            worldRedirectBody[0] = 1;
            FiestaHeadlessLoadClient.WriteFixedAscii(worldRedirectBody.AsSpan(1, 16), worldHost);
            worldRedirectBody[17] = (byte)(worldPort & 0xff);
            worldRedirectBody[18] = (byte)(worldPort >> 8);
            transferKey.CopyTo(worldRedirectBody.AsSpan(19, 32));
            var worldRedirectPayload = BuildPayload(3, 12, worldRedirectBody);

            var fileHashBody = new byte[30];
            fileHashBody[0] = 29;
            Encoding.ASCII.GetBytes(fileHash).CopyTo(fileHashBody, 1);

            var clientPayloads = new[]
            {
                BuildPayload(3, 101, versionBody),
                BuildPayload(3, 4, fileHashBody),
                BuildPayload(3, 11, new[] { worldId })
            };

            var cipher = new FiestaXorCipher(xorPosition);
            var clientStream = new MemoryStream();
            foreach (var payload in clientPayloads)
            {
                var encrypted = payload.ToArray();
                cipher.TransformInPlace(encrypted);
                clientStream.Write(FiestaWireConnection.FramePayload(encrypted));
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
                || profile.WorldId != worldId
                || profile.WorldHost != worldHost
                || profile.WorldPort != worldPort
                || profile.FileHash != fileHash
                || profile.XorPosition != xorPosition
                || !profile.WorldTransferKey.SequenceEqual(transferKey)
                || !profile.GetCapturedClientVersionBody().SequenceEqual(versionBody)
                || profile.GetCapturedFileHashBody() is not { } capturedHashBody
                || !capturedHashBody.SequenceEqual(fileHashBody))
            {
                throw new InvalidDataException(
                    "Capture-Profil veränderte Login-/World-Endpunkt, rohe CH3/101-/CH3/4-Bodies, World-ID, Binär-Key oder XOR-Position.");
            }

            var worldHandshakePayload = BuildPayload(2, 7, new byte[]
            {
                (byte)(worldXorPosition & 0xff),
                (byte)(worldXorPosition >> 8)
            });
            var capturedWorldBody = new byte[320];
            for (var i = 0; i < capturedWorldBody.Length; i++)
                capturedWorldBody[i] = unchecked((byte)(i * 29 + 11));
            transferKey.CopyTo(capturedWorldBody.AsSpan(worldKeyOffset, 32));

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
                                ?? throw new InvalidDataException("Synthetischer binärer CH3/15 WorldClientKey wurde nicht extrahiert.");

            if (capturedWorld.TransferKeyOffset != worldKeyOffset)
                throw new InvalidDataException($"CH3/15 KeyOffset ist {capturedWorld.TransferKeyOffset} statt {worldKeyOffset}.");

            var enriched = profile with
            {
                WorldClientKeyBodyBase64 = Convert.ToBase64String(capturedWorld.Body),
                WorldClientKeyTransferKeyOffset = capturedWorld.TransferKeyOffset,
                WorldTcpStreamId = capturedWorld.TcpStreamId,
                WorldXorPosition = capturedWorld.XorPosition
            };
            enriched.Validate();
            if (!enriched.HasCapturedWorldClientKey)
                throw new InvalidDataException("Capture-Profil markiert den vorhandenen CH3/15-Body nicht als vollständig.");

            var replacementKey = Enumerable.Range(0, 32)
                .Select(i => unchecked((byte)(0x20 + i)))
                .ToArray();
            var materialized = enriched.MaterializeWorldClientKeyBody(replacementKey);
            if (!materialized.AsSpan(0, worldKeyOffset).SequenceEqual(capturedWorldBody.AsSpan(0, worldKeyOffset))
                || !materialized.AsSpan(worldKeyOffset, 32).SequenceEqual(replacementKey)
                || !materialized.AsSpan(worldKeyOffset + 32).SequenceEqual(capturedWorldBody.AsSpan(worldKeyOffset + 32)))
            {
                throw new InvalidDataException("WorldClientKey-Materialisierung veränderte Capture-Bytes außerhalb des binären 32-Byte-Keyfelds.");
            }

            return new FiestaClientCaptureProfileSelfTestResult(
                true,
                "CLIENT CAPTURE PROFILE SELFTEST: PASS · real-layout CH3/101 body + CH3/4 body + binärer CH3/15-Key mit capture-derived Offset.");
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
        byte[]? worldTransferKey = null;
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
                worldTransferKey = packet.Body.AsSpan(19, 32).ToArray();
            }
        }

        if (!handshake.HasValue
            || string.IsNullOrWhiteSpace(worldHost)
            || !worldPort.HasValue
            || worldTransferKey is null
            || worldTransferKey.Length != 32)
        {
            return null;
        }

        var xorPosition = handshake.Value.Body[0] | (handshake.Value.Body[1] << 8);
        if (xorPosition is < 0 or >= FiestaXorCipher.TableLength)
            return null;

        ushort clientYear = 0;
        ushort clientVersion = 0;
        byte[]? versionBody = null;
        byte[]? fileHashBody = null;
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
                case 101 when packet.Body.Length > 0:
                    versionBody = packet.Body.ToArray();
                    // Legacy/emulator captures used a four-byte numeric body.
                    // Real NA2016 uses a larger opaque body, which is replayed byte-for-byte.
                    if (packet.Body.Length == 4)
                    {
                        clientYear = (ushort)(packet.Body[0] | (packet.Body[1] << 8));
                        clientVersion = (ushort)(packet.Body[2] | (packet.Body[3] << 8));
                    }
                    break;

                case 4 when packet.Body.Length > 0:
                    fileHashBody = packet.Body.ToArray();
                    fileHash = TryReadCapturedFileHash(packet.Body);
                    break;

                case 11 when packet.Body.Length >= 1:
                    worldId = packet.Body[0];
                    break;
            }
        }

        if (versionBody is null || !worldId.HasValue)
            return null;

        var endpoint = parsed.Endpoints[serverNode.Value];
        var profile = new FiestaCapturedClientProfile
        {
            LoginHost = endpoint.Host,
            LoginPort = endpoint.Port,
            ClientYear = clientYear,
            ClientVersion = clientVersion,
            ClientVersionBodyBase64 = Convert.ToBase64String(versionBody),
            FileHash = fileHash,
            FileHashBodyBase64 = fileHashBody is null ? string.Empty : Convert.ToBase64String(fileHashBody),
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
        byte[] expectedTransferKey)
    {
        if (string.IsNullOrWhiteSpace(followText)
            || expectedTransferKey is null
            || expectedTransferKey.Length != 32)
        {
            return null;
        }

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

            if (packet.Header != 3 || packet.Type != 15 || packet.Body.Length < expectedTransferKey.Length)
                continue;

            var keyOffset = FindUniqueSequenceOffset(packet.Body, expectedTransferKey);
            if (keyOffset < 0)
                continue;

            return new FiestaWorldClientKeyCapture(
                streamId,
                xorPosition,
                packet.Body.ToArray(),
                keyOffset);
        }

        return null;
    }

    private static string? TryReadCapturedFileHash(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
            return null;

        ReadOnlySpan<byte> text = body;
        var declared = body[0];
        if (declared > 0 && declared <= body.Length - 1)
            text = body.Slice(1, declared);

        var zero = text.IndexOf((byte)0);
        if (zero >= 0)
            text = text[..zero];

        var value = Encoding.ASCII.GetString(text).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int FindUniqueSequenceOffset(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
            return -1;

        var first = haystack.IndexOf(needle);
        if (first < 0)
            return -1;

        var remainderStart = first + 1;
        if (remainderStart < haystack.Length
            && haystack[remainderStart..].IndexOf(needle) >= 0)
        {
            return -1;
        }

        return first;
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
    public string ClientVersionBodyBase64 { get; init; } = string.Empty;
    public string? FileHash { get; init; }
    public string FileHashBodyBase64 { get; init; } = string.Empty;
    public byte WorldId { get; init; }
    public string WorldHost { get; init; } = string.Empty;
    public int WorldPort { get; init; }
    public int TcpStreamId { get; init; } = -1;
    public int XorPosition { get; init; }
    public string WorldClientKeyBodyBase64 { get; init; } = string.Empty;
    public int WorldClientKeyTransferKeyOffset { get; init; } = -1;
    public int WorldTcpStreamId { get; init; } = -1;
    public int WorldXorPosition { get; init; } = -1;
    public string SourceCaptureSha256 { get; init; } = string.Empty;

    [JsonIgnore]
    internal byte[] WorldTransferKey { get; init; } = Array.Empty<byte>();

    [JsonIgnore]
    public bool HasCapturedClientVersion
    {
        get
        {
            try { return Convert.FromBase64String(ClientVersionBodyBase64).Length > 0; }
            catch { return false; }
        }
    }

    [JsonIgnore]
    public bool HasCapturedFileHashBody
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FileHashBodyBase64))
                return false;
            try { return Convert.FromBase64String(FileHashBodyBase64).Length > 0; }
            catch { return false; }
        }
    }

    [JsonIgnore]
    public bool HasCapturedWorldClientKey
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorldClientKeyBodyBase64)
                || WorldClientKeyTransferKeyOffset < 0
                || WorldTcpStreamId < 0
                || WorldXorPosition is < 0 or >= FiestaXorCipher.TableLength)
            {
                return false;
            }

            try
            {
                var body = Convert.FromBase64String(WorldClientKeyBodyBase64);
                return WorldClientKeyTransferKeyOffset + 32 <= body.Length;
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
        if (!HasCapturedClientVersion && (ClientYear == 0 || ClientVersion == 0))
            throw new InvalidDataException("Weder capture-basierter CH3/101-Body noch Legacy ClientYear/ClientVersion sind vorhanden.");
        if (string.IsNullOrWhiteSpace(WorldHost) || WorldPort is <= 0 or > 65535)
            throw new InvalidDataException("WorldHost/WorldPort fehlen im Capture-Profil.");
        if (TcpStreamId < 0)
            throw new InvalidDataException("TcpStreamId fehlt im Capture-Profil.");
        if (XorPosition is < 0 or >= FiestaXorCipher.TableLength)
            throw new InvalidDataException("XOR-Position liegt außerhalb der verifizierten 499-Byte-Tabelle.");

        if (!string.IsNullOrWhiteSpace(ClientVersionBodyBase64))
        {
            try
            {
                if (Convert.FromBase64String(ClientVersionBodyBase64).Length == 0)
                    throw new InvalidDataException("CH3/101 Capture-Body ist leer.");
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("ClientVersionBodyBase64 ist ungültig.", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(FileHashBodyBase64))
        {
            try
            {
                if (Convert.FromBase64String(FileHashBodyBase64).Length == 0)
                    throw new InvalidDataException("CH3/4 FileHash Capture-Body ist leer.");
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("FileHashBodyBase64 ist ungültig.", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(WorldClientKeyBodyBase64))
        {
            byte[] body;
            try { body = Convert.FromBase64String(WorldClientKeyBodyBase64); }
            catch (FormatException ex) { throw new InvalidDataException("WorldClientKeyBodyBase64 ist ungültig.", ex); }
            if (body.Length < 32)
                throw new InvalidDataException($"CH3/15 WorldClientKey-Body ist zu kurz ({body.Length} Byte).");
            if (WorldClientKeyTransferKeyOffset < 0 || WorldClientKeyTransferKeyOffset + 32 > body.Length)
                throw new InvalidDataException("WorldClientKeyTransferKeyOffset liegt außerhalb des capture-basierten CH3/15-Bodys.");
            if (WorldTcpStreamId < 0)
                throw new InvalidDataException("WorldTcpStreamId fehlt für den capture-basierten CH3/15-Body.");
            if (WorldXorPosition is < 0 or >= FiestaXorCipher.TableLength)
                throw new InvalidDataException("WorldXorPosition liegt außerhalb der verifizierten 499-Byte-Tabelle.");
        }
    }

    internal byte[] GetCapturedClientVersionBody()
    {
        Validate();
        if (!HasCapturedClientVersion)
            throw new InvalidDataException("Capture-Profil enthält keinen vollständigen CH3/101-Body.");
        return Convert.FromBase64String(ClientVersionBodyBase64);
    }

    internal byte[]? GetCapturedFileHashBody()
    {
        Validate();
        return HasCapturedFileHashBody
            ? Convert.FromBase64String(FileHashBodyBase64)
            : null;
    }

    public byte[] MaterializeWorldClientKeyBody(byte[] transferKey)
    {
        Validate();
        if (!HasCapturedWorldClientKey)
            throw new InvalidDataException(
                "Capture-Profil enthält keinen vollständigen CH3/15 WorldClientKey-Body. Capture erneut vom Login bis zur Charakterliste importieren.");
        if (transferKey is null || transferKey.Length != 32)
            throw new ArgumentException("World-Transfer-Key muss exakt 32 Binär-Byte lang sein.", nameof(transferKey));

        var body = Convert.FromBase64String(WorldClientKeyBodyBase64);
        transferKey.CopyTo(body.AsSpan(WorldClientKeyTransferKeyOffset, 32));
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

internal sealed record FiestaWorldClientKeyCapture(
    int TcpStreamId,
    int XorPosition,
    byte[] Body,
    int TransferKeyOffset);
