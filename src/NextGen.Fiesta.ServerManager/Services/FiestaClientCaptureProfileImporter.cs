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
                    selected.LoginUsername,
                    selected.WorldTransferMaterial);
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
            WorldClientKeyTransferKeyOffset = worldClientKey.TransferMaterialOffset,
            WorldClientKeyTransferMaterialLength = worldClientKey.TransferMaterialLength,
            WorldClientKeyUsernameOffset = worldClientKey.UsernameOffset,
            WorldClientKeyUsernameLength = worldClientKey.UsernameLength,
            WorldTutorialCancelBodyBase64 = worldClientKey.TutorialCancelBody is null
                ? string.Empty
                : Convert.ToBase64String(worldClientKey.TutorialCancelBody),
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
                $"CH3/15 User {selected.WorldClientKeyUsernameLength} Byte @ {selected.WorldClientKeyUsernameOffset} · " +
                $"Transfermaterial {selected.WorldClientKeyTransferMaterialLength} Byte @ {selected.WorldClientKeyTransferKeyOffset} · " +
                $"{(selected.HasCapturedTutorialCancel ? "Tutorial-Cancel capture-derived" : "Tutorial-Cancel nicht angefordert")} · " +
                $"tcp.stream={selected.TcpStreamId}."
        };
    }

    public static FiestaClientCaptureProfileSelfTestResult RunSelfTest()
    {
        try
        {
            const ushort xorPosition = 321;
            const byte worldId = 3;
            const string fileHash = "33B543B0CA6E7C41E5D1D0651307";
            const string loginUsername = "LoadProbe";
            const ushort worldXorPosition = 211;
            const int worldMaterialOffset = 256;

            var transferMaterial = Enumerable.Range(0, 64)
                .Select(i => unchecked((byte)(0x31 + i * 3)))
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
            transferMaterial.CopyTo(worldRedirectBody.AsSpan(19, 64));
            var worldRedirectPayload = BuildPayload(3, 12, worldRedirectBody);

            var fileHashBody = new byte[30];
            fileHashBody[0] = 29;
            Encoding.ASCII.GetBytes(fileHash).CopyTo(fileHashBody, 1);

            var loginBody = new byte[316];
            FiestaHeadlessLoadClient.WriteFixedAscii(loginBody.AsSpan(0, 260), loginUsername);
            FiestaHeadlessLoadClient.WriteFixedAscii(
                loginBody.AsSpan(260, 32),
                "0123456789abcdef0123456789abcdef");
            FiestaHeadlessLoadClient.WriteFixedAscii(loginBody.AsSpan(296, 8), "Original");

            var clientPayloads = new[]
            {
                BuildPayload(3, 101, versionBody),
                BuildPayload(3, 90, loginBody),
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
                || profile.LoginUsername != loginUsername
                || !profile.WorldTransferMaterial.SequenceEqual(transferMaterial)
                || !profile.GetCapturedClientVersionBody().SequenceEqual(versionBody)
                || profile.GetCapturedFileHashBody() is not { } capturedHashBody
                || !capturedHashBody.SequenceEqual(fileHashBody))
            {
                throw new InvalidDataException(
                    "Capture-Profil veränderte Login-/World-Endpunkt, rohe CH3/101-/CH3/4-Bodies, World-ID, Username, 64-Byte-Transfermaterial oder XOR-Position.");
            }

            var worldHandshakePayload = BuildPayload(2, 7, new byte[]
            {
                (byte)(worldXorPosition & 0xff),
                (byte)(worldXorPosition >> 8)
            });

            var capturedWorldBody = new byte[320];
            FiestaHeadlessLoadClient.WriteFixedAscii(
                capturedWorldBody.AsSpan(0, worldMaterialOffset),
                loginUsername);
            transferMaterial.CopyTo(capturedWorldBody.AsSpan(worldMaterialOffset, 64));

            var worldCipher = new FiestaXorCipher(worldXorPosition);
            var worldClientStream = new MemoryStream();
            foreach (var payload in new[]
                     {
                         BuildPayload(3, 15, capturedWorldBody),
                         BuildPayload(4, 273, new byte[] { 1 })
                     })
            {
                var encrypted = payload.ToArray();
                worldCipher.TransformInPlace(encrypted);
                worldClientStream.Write(FiestaWireConnection.FramePayload(encrypted));
            }

            var worldServerStream = new MemoryStream();
            worldServerStream.Write(FiestaWireConnection.FramePayload(worldHandshakePayload));
            worldServerStream.Write(FiestaWireConnection.FramePayload(
                BuildPayload(4, 272, new byte[] { 0xD8 })));

            var worldFollow =
                "===================================================================\n" +
                "Follow: tcp,raw\n" +
                "Filter: tcp.stream eq 10\n" +
                "Node 0: 127.0.0.1:55002\n" +
                $"Node 1: 127.0.0.1:{worldPort}\n" +
                Convert.ToHexString(worldClientStream.ToArray()).ToLowerInvariant() + "\n" +
                "\t" + Convert.ToHexString(worldServerStream.ToArray()).ToLowerInvariant() + "\n" +
                "===================================================================\n";

            var capturedWorld = TryExtractWorldClientKeyFromFollowText(
                                    worldFollow,
                                    10,
                                    worldPort,
                                    loginUsername,
                                    transferMaterial)
                                ?? throw new InvalidDataException("Synthetischer realformatiger CH3/15-Worldtransfer wurde nicht extrahiert.");

            if (capturedWorld.TransferMaterialOffset != worldMaterialOffset
                || capturedWorld.TransferMaterialLength != 64
                || capturedWorld.UsernameOffset != 0
                || capturedWorld.UsernameLength != worldMaterialOffset
                || capturedWorld.TutorialCancelBody is null
                || !capturedWorld.TutorialCancelBody.SequenceEqual(new byte[] { 1 }))
            {
                throw new InvalidDataException("CH3/15-Feldmetadaten oder Tutorial-Cancel wurden falsch erkannt.");
            }

            var enriched = profile with
            {
                WorldClientKeyBodyBase64 = Convert.ToBase64String(capturedWorld.Body),
                WorldClientKeyTransferKeyOffset = capturedWorld.TransferMaterialOffset,
                WorldClientKeyTransferMaterialLength = capturedWorld.TransferMaterialLength,
                WorldClientKeyUsernameOffset = capturedWorld.UsernameOffset,
                WorldClientKeyUsernameLength = capturedWorld.UsernameLength,
                WorldTutorialCancelBodyBase64 = Convert.ToBase64String(capturedWorld.TutorialCancelBody),
                WorldTcpStreamId = capturedWorld.TcpStreamId,
                WorldXorPosition = capturedWorld.XorPosition
            };
            enriched.Validate();

            if (!enriched.HasCapturedWorldClientKey || !enriched.HasCapturedTutorialCancel)
                throw new InvalidDataException("Capture-Profil markiert CH3/15 oder Tutorial-Cancel nicht als vollständig.");

            var replacementMaterial = Enumerable.Range(0, 64)
                .Select(i => unchecked((byte)(0xE0 - i)))
                .ToArray();
            const string replacementUsername = "Ramp0001";
            var materialized = enriched.MaterializeWorldClientKeyBody(
                replacementUsername,
                replacementMaterial);

            if (FiestaHeadlessLoadClient.ReadFixedAscii(
                    materialized.AsSpan(enriched.WorldClientKeyUsernameOffset, enriched.WorldClientKeyUsernameLength))
                != replacementUsername
                || !materialized.AsSpan(
                        enriched.WorldClientKeyTransferKeyOffset,
                        enriched.WorldClientKeyTransferMaterialLength)
                    .SequenceEqual(replacementMaterial)
                || !enriched.GetTutorialCancelBody().SequenceEqual(new byte[] { 1 }))
            {
                throw new InvalidDataException("CH3/15-Username/Transfermaterial oder capture-basierter Tutorial-Cancel wurde falsch materialisiert.");
            }

            return new FiestaClientCaptureProfileSelfTestResult(
                true,
                "CLIENT CAPTURE PROFILE SELFTEST: PASS · real-layout CH3/101/CH3/4 · CH3/15 Username[256] + 64-Byte Binär-Transfermaterial · Tutorial-Cancel capture-basiert.");
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
        byte[]? worldTransferMaterial = null;
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
                var materialLength = packet.Body.Length >= 83 ? 64 : 32;
                worldTransferMaterial = packet.Body.AsSpan(19, materialLength).ToArray();
            }
        }

        if (!handshake.HasValue
            || string.IsNullOrWhiteSpace(worldHost)
            || !worldPort.HasValue
            || worldTransferMaterial is null
            || worldTransferMaterial.Length is not (32 or 64))
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
        string? loginUsername = null;

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

                case 90 when packet.Body.Length >= 260:
                    loginUsername = FiestaHeadlessLoadClient.ReadFixedAscii(packet.Body.AsSpan(0, 260));
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

        if (versionBody is null || !worldId.HasValue || string.IsNullOrWhiteSpace(loginUsername))
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
            LoginUsername = loginUsername,
            WorldTransferMaterial = worldTransferMaterial
        };
        profile.Validate();
        return profile;
    }

    internal static FiestaWorldClientKeyCapture? TryExtractWorldClientKeyFromFollowText(
        string followText,
        int streamId,
        int worldPort,
        string expectedUsername,
        byte[] expectedTransferMaterial)
    {
        if (string.IsNullOrWhiteSpace(followText)
            || string.IsNullOrWhiteSpace(expectedUsername)
            || expectedTransferMaterial is null
            || expectedTransferMaterial.Length is not (32 or 64))
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

        var serverPackets = ParseFrames(serverData)
            .Select(FiestaPacket.FromPayload)
            .ToList();
        var handshake = serverPackets
            .FirstOrDefault(x => x.Header == 2 && x.Type == 7 && x.Body.Length >= 2);
        if (handshake.Body is null || handshake.Body.Length < 2)
            return null;

        var tutorialPromptSeen = serverPackets.Any(x => x.Header == 4 && x.Type == 272);
        var xorPosition = handshake.Body[0] | (handshake.Body[1] << 8);
        if (xorPosition is < 0 or >= FiestaXorCipher.TableLength)
            return null;

        var usernameBytes = Encoding.ASCII.GetBytes(expectedUsername);
        FiestaWorldClientKeyCapture? captured = null;
        byte[]? tutorialCancelBody = null;

        var cipher = new FiestaXorCipher(xorPosition);
        foreach (var encryptedPayload in ParseFrames(clientData))
        {
            var decrypted = encryptedPayload.ToArray();
            cipher.TransformInPlace(decrypted);

            FiestaPacket packet;
            try { packet = FiestaPacket.FromPayload(decrypted); }
            catch { continue; }

            if (packet.Header == 3 && packet.Type == 15)
            {
                var transferOffset = FindUniqueSequenceOffset(packet.Body, expectedTransferMaterial);
                var usernameOffset = FindUniqueSequenceOffset(packet.Body, usernameBytes);
                if (transferOffset < 0 || usernameOffset < 0 || usernameOffset >= transferOffset)
                    continue;

                var usernameLength = transferOffset - usernameOffset;
                if (usernameLength < usernameBytes.Length)
                    continue;

                var paddingValid = true;
                for (var i = usernameOffset + usernameBytes.Length; i < transferOffset; i++)
                {
                    if (packet.Body[i] == 0)
                        continue;
                    paddingValid = false;
                    break;
                }

                if (!paddingValid)
                    continue;

                captured = new FiestaWorldClientKeyCapture(
                    streamId,
                    xorPosition,
                    packet.Body.ToArray(),
                    transferOffset,
                    expectedTransferMaterial.Length,
                    usernameOffset,
                    usernameLength,
                    tutorialCancelBody);
                continue;
            }

            if (tutorialPromptSeen
                && packet.Header == 4
                && packet.Type == 273
                && packet.Body.Length > 0)
            {
                if (tutorialCancelBody is not null
                    && !tutorialCancelBody.SequenceEqual(packet.Body))
                {
                    return null;
                }

                tutorialCancelBody = packet.Body.ToArray();
            }
        }

        return captured is null
            ? null
            : captured with { TutorialCancelBody = tutorialCancelBody };
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
    public int WorldClientKeyTransferMaterialLength { get; init; }
    public int WorldClientKeyUsernameOffset { get; init; } = -1;
    public int WorldClientKeyUsernameLength { get; init; }
    public string WorldTutorialCancelBodyBase64 { get; init; } = string.Empty;
    public int WorldTcpStreamId { get; init; } = -1;
    public int WorldXorPosition { get; init; } = -1;
    public string SourceCaptureSha256 { get; init; } = string.Empty;

    [JsonIgnore]
    internal string LoginUsername { get; init; } = string.Empty;

    [JsonIgnore]
    internal byte[] WorldTransferMaterial { get; init; } = Array.Empty<byte>();

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
                || WorldClientKeyTransferMaterialLength is not (32 or 64)
                || WorldClientKeyUsernameOffset < 0
                || WorldClientKeyUsernameLength <= 0
                || WorldTcpStreamId < 0
                || WorldXorPosition is < 0 or >= FiestaXorCipher.TableLength)
            {
                return false;
            }

            try
            {
                var body = Convert.FromBase64String(WorldClientKeyBodyBase64);
                return WorldClientKeyTransferKeyOffset + WorldClientKeyTransferMaterialLength <= body.Length
                       && WorldClientKeyUsernameOffset + WorldClientKeyUsernameLength <= body.Length;
            }
            catch
            {
                return false;
            }
        }
    }

    [JsonIgnore]
    public bool HasCapturedTutorialCancel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorldTutorialCancelBodyBase64))
                return false;
            try { return Convert.FromBase64String(WorldTutorialCancelBodyBase64).Length > 0; }
            catch { return false; }
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
            if (body.Length < 1)
                throw new InvalidDataException("CH3/15 WorldClientKey-Body ist leer.");
            if (WorldClientKeyTransferKeyOffset < 0
                || WorldClientKeyTransferMaterialLength is not (32 or 64)
                || WorldClientKeyTransferKeyOffset + WorldClientKeyTransferMaterialLength > body.Length)
                throw new InvalidDataException("WorldClientKey-Transfermaterial liegt außerhalb des capture-basierten CH3/15-Bodys.");
            if (WorldClientKeyUsernameOffset < 0
                || WorldClientKeyUsernameLength <= 0
                || WorldClientKeyUsernameOffset + WorldClientKeyUsernameLength > body.Length)
                throw new InvalidDataException("WorldClientKey-Usernamefeld liegt außerhalb des capture-basierten CH3/15-Bodys.");
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

    public byte[] MaterializeWorldClientKeyBody(
        string username,
        byte[] transferMaterial)
    {
        Validate();
        if (!HasCapturedWorldClientKey)
            throw new InvalidDataException(
                "Capture-Profil enthält keinen vollständigen CH3/15 WorldClientKey-Body mit Feldmetadaten. Capture erneut importieren.");
        if (string.IsNullOrWhiteSpace(username)
            || Encoding.ASCII.GetByteCount(username) > WorldClientKeyUsernameLength)
        {
            throw new ArgumentException(
                $"World-Username fehlt oder ist länger als {WorldClientKeyUsernameLength} ASCII-Byte.",
                nameof(username));
        }

        if (transferMaterial is null
            || transferMaterial.Length != WorldClientKeyTransferMaterialLength)
        {
            throw new ArgumentException(
                $"World-Transfermaterial muss exakt {WorldClientKeyTransferMaterialLength} Binär-Byte lang sein.",
                nameof(transferMaterial));
        }

        var body = Convert.FromBase64String(WorldClientKeyBodyBase64);
        FiestaHeadlessLoadClient.WriteFixedAscii(
            body.AsSpan(WorldClientKeyUsernameOffset, WorldClientKeyUsernameLength),
            username);
        transferMaterial.CopyTo(
            body.AsSpan(WorldClientKeyTransferKeyOffset, WorldClientKeyTransferMaterialLength));
        return body;
    }

    public byte[] GetTutorialCancelBody()
    {
        Validate();
        if (!HasCapturedTutorialCancel)
            throw new InvalidDataException(
                "Capture-Profil enthält keine capture-basierte CH4/273 Tutorial-Cancel-Antwort.");
        return Convert.FromBase64String(WorldTutorialCancelBodyBase64);
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
    int TransferMaterialOffset,
    int TransferMaterialLength,
    int UsernameOffset,
    int UsernameLength,
    byte[]? TutorialCancelBody);
