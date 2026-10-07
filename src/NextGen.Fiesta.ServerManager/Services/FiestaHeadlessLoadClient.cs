using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Minimal NA2016 client used only for controlled load testing against the user's own server.
/// It follows the real Login -> World -> Zone protocol path and intentionally does not implement gameplay.
/// Server -> client packets are plaintext. Client -> server packets use the 499-byte NA2016 XOR stream.
/// </summary>
public sealed class FiestaHeadlessLoadClient
{
    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(15);

    public async Task<FiestaHeadlessProbeResult> ProbeAndHoldAsync(
        FiestaHeadlessProbeOptions options,
        FiestaLoadClientCredential credential,
        CancellationToken cancellationToken = default,
        Action<FiestaHeadlessClientProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        options.Validate();
        credential.Validate();

        var stage = FiestaLoadClientStage.None;
        void SetStage(FiestaLoadClientStage next, string detail = "")
        {
            stage = next;
            progress?.Invoke(new FiestaHeadlessClientProgress(
                credential.Username,
                credential.CharacterName,
                next,
                detail,
                DateTimeOffset.UtcNow));
        }

        SetStage(FiestaLoadClientStage.None, "Start");
        try
        {
            var passwordMd5 = credential.ResolvePasswordMd5();
            var clientProfile = string.IsNullOrWhiteSpace(options.ClientCaptureProfilePath)
                ? null
                : FiestaCapturedClientProfile.Load(options.ClientCaptureProfilePath);

            FiestaEndpointRedirect world;
            await using (var login = await FiestaWireConnection.ConnectAsync(
                             options.LoginHost,
                             options.LoginPort,
                             options.StepTimeout,
                             cancellationToken))
            {
                SetStage(FiestaLoadClientStage.LoginConnected);

                await login.SendPacketAsync(3, 101, BuildVersionBody(options, clientProfile), cancellationToken);
                await WaitForAsync(login, 3, 103, options.StepTimeout, cancellationToken);
                SetStage(FiestaLoadClientStage.VersionAccepted);

                // Real NA2016 login capture order is significant:
                // CH3/90 Login is sent first, then CH3/4 FileHash. The server acknowledges
                // CH3/4 with SH3/5 and only afterwards emits SH3/10 LoginAccepted.
                // Sending CH3/4 before CH3/90 makes the original Login server close the socket.
                var authenticationPackets = BuildLoginAuthenticationPackets(
                    options,
                    clientProfile,
                    credential,
                    passwordMd5);

                await login.SendPacketAsync(
                    authenticationPackets[0].Header,
                    authenticationPackets[0].Type,
                    authenticationPackets[0].Body,
                    cancellationToken);

                if (authenticationPackets.Count > 1)
                {
                    await login.SendPacketAsync(
                        authenticationPackets[1].Header,
                        authenticationPackets[1].Type,
                        authenticationPackets[1].Body,
                        cancellationToken);
                    await WaitForAsync(login, 3, 5, options.StepTimeout, cancellationToken);
                }

                await WaitForLoginAcceptedAsync(
                    login,
                    credential.Username,
                    options.StepTimeout,
                    cancellationToken);
                SetStage(FiestaLoadClientStage.LoginAuthenticated);

                await login.SendPacketAsync(3, 11, new[] { options.WorldId }, cancellationToken);
                var worldRedirect = await WaitForAsync(login, 3, 12, options.StepTimeout, cancellationToken);
                world = ParseWorldRedirect(worldRedirect.Body);
                SetStage(FiestaLoadClientStage.WorldRedirectReceived);
            }

            FiestaEndpointRedirect zone;
            ushort randomId;
            byte characterCount;
            await using (var worldConnection = await FiestaWireConnection.ConnectAsync(
                             world.Host,
                             world.Port,
                             options.StepTimeout,
                             cancellationToken))
            {
                SetStage(FiestaLoadClientStage.WorldConnected);

                await worldConnection.SendPacketAsync(
                    3,
                    15,
                    BuildWorldTransferBody(options, clientProfile, credential.Username, world.TransferMaterial),
                    cancellationToken);
                var characterList = await WaitForAsync(worldConnection, 3, 20, options.StepTimeout, cancellationToken);
                if (characterList.Body.Length < 3)
                    throw new InvalidDataException("World CharacterList ist zu kurz.");

                randomId = BinaryPrimitives.ReadUInt16LittleEndian(characterList.Body.AsSpan(0, 2));
                characterCount = characterList.Body[2];
                if (characterCount == 0)
                {
                    if (!credential.CreateCharacterIfMissing)
                        throw new InvalidOperationException("Der Testaccount besitzt keinen Charakter und Auto-Create ist nicht freigegeben.");
                    if (string.IsNullOrWhiteSpace(options.CharacterCreateTemplatePath))
                        throw new InvalidOperationException("Auto-Create benötigt ein capture-basiertes CH5/1 CharacterCreate-Template.");

                    var createTemplate = FiestaCharacterCreateTemplate.Load(options.CharacterCreateTemplatePath);
                    var createPayload = createTemplate.Materialize(credential.Slot, credential.CharacterName);
                    await worldConnection.SendDecryptedPayloadAsync(createPayload, cancellationToken);
                    await WaitForAsync(worldConnection, 5, 6, options.StepTimeout, cancellationToken);
                    characterCount = 1;
                    SetStage(FiestaLoadClientStage.CharacterCreated);
                }

                await worldConnection.SendPacketAsync(4, 1, new[] { credential.Slot }, cancellationToken);
                var zoneRedirect = await WaitForZoneRedirectAsync(
                    worldConnection,
                    clientProfile,
                    options.StepTimeout,
                    cancellationToken);
                zone = ParseZoneRedirect(zoneRedirect.Body);
                SetStage(FiestaLoadClientStage.ZoneRedirectReceived);
            }

            await using var zoneConnection = await FiestaWireConnection.ConnectAsync(
                zone.Host,
                zone.Port,
                options.StepTimeout,
                cancellationToken);
            SetStage(FiestaLoadClientStage.ZoneConnected);

            var zoneTransferPayload = BuildZoneTransferPayload(options, credential, randomId);
            await zoneConnection.SendDecryptedPayloadAsync(zoneTransferPayload, cancellationToken);

            // A successful original-client login emits the character-information cascade and ends it with SH4/72.
            // Waiting for that marker prevents a mere TCP connection from being counted as a successful player login.
            await WaitForAsync(zoneConnection, 4, 72, options.ZoneLoginTimeout, cancellationToken);
            SetStage(FiestaLoadClientStage.ZoneAuthenticated);

            await zoneConnection.SendPacketAsync(6, 3, ReadOnlyMemory<byte>.Empty, cancellationToken);
            SetStage(FiestaLoadClientStage.ClientReady);

            var holdFor = options.HoldDuration;
            if (holdFor > TimeSpan.Zero)
            {
                SetStage(FiestaLoadClientStage.Holding);
                await HoldSessionAsync(zoneConnection, holdFor, cancellationToken);
            }

            SetStage(FiestaLoadClientStage.Completed);
            return new FiestaHeadlessProbeResult
            {
                Success = true,
                Stage = stage,
                WorldHost = world.Host,
                WorldPort = world.Port,
                ZoneHost = zone.Host,
                ZonePort = zone.Port,
                RandomId = randomId,
                CharacterCount = characterCount,
                Detail = $"HEADLESS CLIENT PASS · Login/World/Zone vollständig · Charakter '{credential.CharacterName}' · Zone {zone.Host}:{zone.Port}."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.Invoke(new FiestaHeadlessClientProgress(
                credential.Username,
                credential.CharacterName,
                stage,
                "Abgebrochen",
                DateTimeOffset.UtcNow,
                Failed: true));
            return new FiestaHeadlessProbeResult
            {
                Success = false,
                Stage = stage,
                Cancelled = true,
                Detail = $"Headless-Client bei Stufe {stage} abgebrochen."
            };
        }
        catch (Exception ex)
        {
            progress?.Invoke(new FiestaHeadlessClientProgress(
                credential.Username,
                credential.CharacterName,
                stage,
                ex.Message,
                DateTimeOffset.UtcNow,
                Failed: true));
            return new FiestaHeadlessProbeResult
            {
                Success = false,
                Stage = stage,
                Detail = $"Headless-Client bei Stufe {stage} fehlgeschlagen: {ex.Message}"
            };
        }
    }

    public static FiestaProtocolSelfTestResult RunProtocolSelfTest()
    {
        try
        {
            var version = BuildPacketPayload(3, 101, BuildLegacyVersionBody(2016, 2));
            if (version.Length != 6 || BinaryPrimitives.ReadUInt16LittleEndian(version) != PackOpcode(3, 101))
                throw new InvalidDataException("Version-Paket ist strukturell falsch.");

            var login = BuildPacketPayload(3, 90, BuildLoginBody("probe", "21232f297a57a5a743894a0e4a801fc3", "Original"));
            if (login.Length != 318)
                throw new InvalidDataException($"Reales NA2016-Loginpaket muss 318 Byte Payload haben, erzeugt wurden {login.Length}.");

            var authSequence = BuildLoginAuthenticationPackets(
                new FiestaHeadlessProbeOptions
                {
                    LoginHost = "127.0.0.1",
                    LoginPort = 9010,
                    ClientYear = 2016,
                    ClientVersion = 2,
                    FileHash = "33B543B0CA6E7C41E5D1D0651307"
                },
                profile: null,
                new FiestaLoadClientCredential
                {
                    Username = "probe",
                    PasswordMd5 = "21232f297a57a5a743894a0e4a801fc3",
                    CharacterName = "probechar"
                },
                "21232f297a57a5a743894a0e4a801fc3");
            if (authSequence.Count != 2
                || authSequence[0].Header != 3
                || authSequence[0].Type != 90
                || authSequence[1].Header != 3
                || authSequence[1].Type != 4)
            {
                throw new InvalidDataException(
                    "Reale Login-Reihenfolge muss CH3/90 Login vor CH3/4 FileHash senden.");
            }

            var crypto = new FiestaXorCipher(123);
            var encrypted = login.ToArray();
            crypto.TransformInPlace(encrypted);
            var decrypt = new FiestaXorCipher(123);
            decrypt.TransformInPlace(encrypted);
            if (!encrypted.SequenceEqual(login))
                throw new InvalidDataException("XOR-Roundtrip ist nicht symmetrisch.");

            var frame = FiestaWireConnection.FramePayload(login);
            if (frame.Length != 321 || frame[0] != 0 || frame[1] != 0x3E || frame[2] != 0x01)
                throw new InvalidDataException("Großes NA2016-Length-Prefix ist falsch.");

            var handshake = new byte[] { 0x07, 0x08, 0x7B, 0x00 };
            var parsed = FiestaPacket.FromPayload(handshake);
            if (parsed.Header != 2 || parsed.Type != 7 || BinaryPrimitives.ReadUInt16LittleEndian(parsed.Body) != 123)
                throw new InvalidDataException("Handshake-Opcode/Position wird falsch dekodiert.");

            var redirectMaterial = Enumerable.Range(0, 64)
                .Select(i => unchecked((byte)(i * 5 + 3)))
                .ToArray();
            var redirectBody = new byte[83];
            redirectBody[0] = 6;
            WriteFixedAscii(redirectBody.AsSpan(1, 16), "127.0.0.1");
            BinaryPrimitives.WriteUInt16LittleEndian(redirectBody.AsSpan(17, 2), 9013);
            redirectMaterial.CopyTo(redirectBody.AsSpan(19, 64));
            var redirect = ParseWorldRedirect(redirectBody);
            if (redirect.Host != "127.0.0.1"
                || redirect.Port != 9013
                || !redirect.TransferMaterial.SequenceEqual(redirectMaterial))
            {
                throw new InvalidDataException("Binäres 64-Byte-World-Transfermaterial wird nicht verlustfrei geparst.");
            }

            return new FiestaProtocolSelfTestResult(
                true,
                "NA2016 PROTOCOL SELFTEST: PASS · Framing, Opcode, Login-318, CH3/90→CH3/4 Reihenfolge, XOR und 64-Byte World-Transfermaterial bestätigt.");
        }
        catch (Exception ex)
        {
            return new FiestaProtocolSelfTestResult(false, "NA2016 PROTOCOL SELFTEST: FAIL · " + ex.Message);
        }
    }

    private static async Task HoldSessionAsync(
        FiestaWireConnection connection,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var wait = remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10);
            try
            {
                var packet = await connection.ReadPacketAsync(wait, cancellationToken);
                if (packet.Header == 2 && packet.Type == 4)
                    await connection.SendPacketAsync(2, 5, ReadOnlyMemory<byte>.Empty, cancellationToken);
                else if (packet.Header == 4 && packet.Type == 2)
                    throw new InvalidOperationException("Zone meldete SH4/2 ConnectError während des Haltens.");
            }
            catch (TimeoutException)
            {
                // No incoming packet in this interval is fine; the socket remains open.
            }
        }
    }

    private static async Task<FiestaPacket> WaitForZoneRedirectAsync(
        FiestaWireConnection connection,
        FiestaCapturedClientProfile? clientProfile,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var packet = await connection.ReadPacketAsync(remaining, cancellationToken);

            if (packet.Header == 2 && packet.Type == 4)
            {
                await connection.SendPacketAsync(2, 5, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            // Real NA2016 capture: after character selection the World server may ask SH4/272
            // whether to enter the tutorial. Replay only the exact CH4/273 Cancel body captured
            // from the real client instead of inventing a response.
            if (packet.Header == 4 && packet.Type == 272)
            {
                if (clientProfile is null || !clientProfile.HasCapturedTutorialCancel)
                {
                    throw new InvalidOperationException(
                        "World fordert SH4/272 Tutorial-Entscheidung an, aber das Client-Capture-Profil enthält keine " +
                        "capture-basierte CH4/273-Cancel-Antwort. Capture mit neuem Charakter und Tutorial-Abbruch erneut importieren.");
                }

                await connection.SendPacketAsync(
                    4,
                    273,
                    clientProfile.GetTutorialCancelBody(),
                    cancellationToken);
                continue;
            }

            if (packet.Header == 3 && packet.Type == 9)
                throw new InvalidOperationException("Login/World meldete SH3/9 Error.");
            if (packet.Header == 4 && packet.Type == 2)
                throw new InvalidOperationException("World/Zone meldete SH4/2 ConnectError.");
            if (packet.Header == 5 && packet.Type == 4)
            {
                var code = packet.Body.Length >= 2
                    ? BinaryPrimitives.ReadUInt16LittleEndian(packet.Body.AsSpan(0, 2))
                    : 0;
                throw new InvalidOperationException($"World meldete SH5/4 CharacterCreationError ({code}).");
            }

            if (packet.Header == 4 && packet.Type == 3)
                return packet;
        }

        throw new TimeoutException(
            $"ZoneRedirect SH4/3 wurde innerhalb von {timeout.TotalSeconds:N0}s nicht empfangen.");
    }

    private static async Task<FiestaPacket> WaitForLoginAcceptedAsync(
        FiestaWireConnection connection,
        string username,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var packet = await connection.ReadPacketAsync(remaining, cancellationToken);

            if (packet.Header == 2 && packet.Type == 4)
            {
                await connection.SendPacketAsync(2, 5, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            if (packet.Header == 3 && packet.Type == 9)
            {
                var hint = username.StartsWith("r_", StringComparison.OrdinalIgnoreCase)
                    ? "Der r_-Auto-Register-Login wurde vom Server abgelehnt."
                    : "Der Account existiert vermutlich nicht. Für diesen NA2016-Server erzeugt ein Username mit Präfix r_ beim ersten Login automatisch einen Account.";
                throw new InvalidOperationException($"Loginserver meldete SH3/9 für '{username}'. {hint}");
            }

            if (packet.Header == 3 && packet.Type == 10)
                return packet;
        }

        throw new TimeoutException(
            $"LoginAccepted SH3/10 wurde innerhalb von {timeout.TotalSeconds:N0}s nicht empfangen.");
    }

    private static async Task<FiestaPacket> WaitForAsync(
        FiestaWireConnection connection,
        int expectedHeader,
        int expectedType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var packet = await connection.ReadPacketAsync(remaining, cancellationToken);
            if (packet.Header == 2 && packet.Type == 4)
            {
                await connection.SendPacketAsync(2, 5, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            if (packet.Header == 3 && packet.Type == 9)
                throw new InvalidOperationException("Login/World meldete SH3/9 Error.");
            if (packet.Header == 4 && packet.Type == 2)
                throw new InvalidOperationException("World/Zone meldete SH4/2 ConnectError.");
            if (packet.Header == 5 && packet.Type == 4)
            {
                var code = packet.Body.Length >= 2
                    ? BinaryPrimitives.ReadUInt16LittleEndian(packet.Body.AsSpan(0, 2))
                    : 0;
                throw new InvalidOperationException($"World meldete SH5/4 CharacterCreationError ({code}).");
            }
            if (packet.Header == expectedHeader && packet.Type == expectedType)
                return packet;
        }

        throw new TimeoutException($"Erwartetes Paket SH{expectedHeader}/{expectedType} wurde innerhalb von {timeout.TotalSeconds:N0}s nicht empfangen.");
    }

    private static byte[] BuildVersionBody(
        FiestaHeadlessProbeOptions options,
        FiestaCapturedClientProfile? profile)
    {
        if (profile?.HasCapturedClientVersion == true)
            return profile.GetCapturedClientVersionBody();

        return BuildLegacyVersionBody(options.ClientYear, options.ClientVersion);
    }

    private static byte[] BuildLegacyVersionBody(ushort year, ushort version)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), year);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2, 2), version);
        return body;
    }

    private static byte[]? BuildFileHashBody(
        FiestaHeadlessProbeOptions options,
        FiestaCapturedClientProfile? profile)
    {
        var captured = profile?.GetCapturedFileHashBody();
        if (captured is not null)
            return captured;

        return string.IsNullOrWhiteSpace(options.FileHash)
            ? null
            : BuildNullTerminatedAscii(options.FileHash);
    }

    private static IReadOnlyList<FiestaOutboundPacket> BuildLoginAuthenticationPackets(
        FiestaHeadlessProbeOptions options,
        FiestaCapturedClientProfile? profile,
        FiestaLoadClientCredential credential,
        string passwordMd5)
    {
        var packets = new List<FiestaOutboundPacket>(2)
        {
            new(
                3,
                90,
                BuildLoginBody(credential.Username, passwordMd5, options.ClientTag))
        };

        var fileHashBody = BuildFileHashBody(options, profile);
        if (fileHashBody is not null)
            packets.Add(new FiestaOutboundPacket(3, 4, fileHashBody));

        return packets;
    }

    private static byte[] BuildLoginBody(string username, string passwordMd5, string clientTag)
    {
        var body = new byte[316];
        WriteFixedAscii(body.AsSpan(0, 260), username);
        WriteFixedAscii(body.AsSpan(260, 32), passwordMd5);
        // 4 bytes padding [292..295] remain zero.
        WriteFixedAscii(body.AsSpan(296, 8), clientTag);
        // 12 bytes padding [304..315] remain zero.
        return body;
    }

    private static byte[] BuildWorldTransferBody(
        FiestaHeadlessProbeOptions options,
        FiestaCapturedClientProfile? profile,
        string username,
        byte[] transferMaterial)
    {
        if (profile is not null)
        {
            if (!profile.HasCapturedWorldClientKey)
            {
                throw new InvalidDataException(
                    "Client-Capture-Profil enthält keinen vollständigen CH3/15 WorldClientKey-Body.");
            }

            return profile.MaterializeWorldClientKeyBody(username, transferMaterial);
        }

        if (!options.AllowEmulatorWorldClientKeyFallback)
        {
            throw new InvalidOperationException(
                "Für den Original-NA2016-Server ist ein capture-basierter CH3/15 WorldClientKey-Body erforderlich. " +
                "Der Emulatorfallback wird nicht als Originalserver-Proof verwendet.");
        }

        if (transferMaterial is null || transferMaterial.Length is not (32 or 64))
            throw new InvalidDataException("World-Redirect lieferte kein vollständiges 32-/64-Byte-Transfermaterial.");

        var body = new byte[18 + transferMaterial.Length];
        transferMaterial.CopyTo(body.AsSpan(18));
        return body;
    }

    private static byte[] BuildZoneTransferPayload(
        FiestaHeadlessProbeOptions options,
        FiestaLoadClientCredential credential,
        ushort randomId)
    {
        if (!string.IsNullOrWhiteSpace(options.ZoneTransferTemplatePath))
        {
            var template = FiestaZoneTransferTemplate.Load(options.ZoneTransferTemplatePath);
            return template.Materialize(randomId, credential.CharacterName);
        }

        if (!options.AllowEmulatorSizedZoneTransfer)
            throw new InvalidOperationException(
                "Für den Original-NA2016-Server ist ein echter entschlüsselter CH6/1-Zone-Transfer-Template erforderlich. " +
                "Die alte 832-Byte-Emulator-Annahme wird nicht als Originalserver-Proof verwendet.");

        // Emulator-compatible research fallback only: opcode + randomID + charname[16] + checksum[832].
        var body = new byte[2 + 16 + 832];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), randomId);
        WriteFixedAscii(body.AsSpan(2, 16), credential.CharacterName);
        return BuildPacketPayload(6, 1, body);
    }

    private static FiestaEndpointRedirect ParseWorldRedirect(ReadOnlySpan<byte> body)
    {
        if (body.Length < 51)
            throw new InvalidDataException($"World-Redirect ist zu kurz ({body.Length} Byte). Erwartet werden mindestens 51 Byte.");
        var host = ReadFixedAscii(body.Slice(1, 16));
        var port = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(17, 2));
        var materialLength = body.Length >= 83 ? 64 : 32;
        var transferMaterial = body.Slice(19, materialLength).ToArray();
        if (string.IsNullOrWhiteSpace(host)
            || port == 0
            || transferMaterial.All(x => x == 0))
        {
            throw new InvalidDataException(
                "World-Redirect enthält Host, Port oder das binäre 32-/64-Byte-Transfermaterial nicht vollständig.");
        }

        return new FiestaEndpointRedirect(host, port, transferMaterial);
    }

    private static FiestaEndpointRedirect ParseZoneRedirect(ReadOnlySpan<byte> body)
    {
        if (body.Length < 18)
            throw new InvalidDataException($"Zone-Redirect ist zu kurz ({body.Length} Byte). Erwartet werden mindestens 18 Byte.");
        var host = ReadFixedAscii(body.Slice(0, 16));
        var port = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(16, 2));
        if (string.IsNullOrWhiteSpace(host) || port == 0)
            throw new InvalidDataException("Zone-Redirect enthält Host oder Port nicht vollständig.");
        return new FiestaEndpointRedirect(host, port, Array.Empty<byte>());
    }

    private static byte[] BuildNullTerminatedAscii(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        var result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        return result;
    }

    private static byte[] BuildPacketPayload(int header, int type, ReadOnlySpan<byte> body)
    {
        var payload = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), PackOpcode(header, type));
        body.CopyTo(payload.AsSpan(2));
        return payload;
    }

    internal static ushort PackOpcode(int header, int type)
    {
        if (header is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(header));
        if (type is < 0 or > 1023) throw new ArgumentOutOfRangeException(nameof(type));
        return checked((ushort)((header << 10) | type));
    }

    internal static void WriteFixedAscii(Span<byte> target, string value)
    {
        target.Clear();
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, target.Length)).CopyTo(target);
    }

    internal static string ReadFixedAscii(ReadOnlySpan<byte> source)
    {
        var zero = source.IndexOf((byte)0);
        if (zero >= 0) source = source[..zero];
        return Encoding.ASCII.GetString(source).Trim();
    }

    private sealed record FiestaOutboundPacket(int Header, int Type, byte[] Body);

    private sealed record FiestaEndpointRedirect(string Host, int Port, byte[] TransferMaterial);
}

public enum FiestaLoadClientStage
{
    None,
    LoginConnected,
    VersionAccepted,
    LoginAuthenticated,
    WorldRedirectReceived,
    WorldConnected,
    CharacterCreated,
    ZoneRedirectReceived,
    ZoneConnected,
    ZoneAuthenticated,
    ClientReady,
    Holding,
    Completed
}

public sealed class FiestaHeadlessProbeOptions
{
    public string LoginHost { get; init; } = "127.0.0.1";
    public int LoginPort { get; init; } = 9010;
    public byte WorldId { get; init; }
    public ushort ClientYear { get; init; } = 2016;
    public ushort ClientVersion { get; init; } = 2;
    public string ClientTag { get; init; } = "Original";
    public string? FileHash { get; init; }
    public string? ClientCaptureProfilePath { get; init; }
    public string? ZoneTransferTemplatePath { get; init; }
    public string? CharacterCreateTemplatePath { get; init; }
    public bool AllowEmulatorWorldClientKeyFallback { get; init; }
    public bool AllowEmulatorSizedZoneTransfer { get; init; }
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ZoneLoginTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HoldDuration { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LoginHost)) throw new ArgumentException("LoginHost fehlt.");
        if (LoginPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(LoginPort));
        if (ClientYear == 0 || ClientVersion == 0) throw new ArgumentException("ClientYear/ClientVersion müssen positiv sein.");
        if (ClientTag.Length > 8) throw new ArgumentException("ClientTag darf maximal 8 ASCII-Zeichen lang sein.");
        if (StepTimeout <= TimeSpan.Zero || ZoneLoginTimeout <= TimeSpan.Zero) throw new ArgumentException("Timeouts müssen positiv sein.");
        if (HoldDuration < TimeSpan.Zero) throw new ArgumentException("HoldDuration darf nicht negativ sein.");
        if (!string.IsNullOrWhiteSpace(ClientCaptureProfilePath) && !File.Exists(ClientCaptureProfilePath))
            throw new FileNotFoundException("Client-Capture-Profil wurde nicht gefunden.", ClientCaptureProfilePath);
        if (!string.IsNullOrWhiteSpace(ZoneTransferTemplatePath) && !File.Exists(ZoneTransferTemplatePath))
            throw new FileNotFoundException("Zone-Transfer-Template wurde nicht gefunden.", ZoneTransferTemplatePath);
        if (!string.IsNullOrWhiteSpace(CharacterCreateTemplatePath) && !File.Exists(CharacterCreateTemplatePath))
            throw new FileNotFoundException("CharacterCreate-Template wurde nicht gefunden.", CharacterCreateTemplatePath);
    }
}

public sealed class FiestaLoadClientCredential
{
    public string Username { get; init; } = string.Empty;
    public string? Password { get; init; }
    public string? PasswordMd5 { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public byte Slot { get; init; }
    public bool CreateCharacterIfMissing { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Username) || Username.Length > 32)
            throw new ArgumentException("Username fehlt oder ist länger als 32 Zeichen.");
        if (string.IsNullOrWhiteSpace(CharacterName) || CharacterName.Length > 16)
            throw new ArgumentException("CharacterName fehlt oder ist länger als 16 Zeichen.");
        if (Slot > 10) throw new ArgumentOutOfRangeException(nameof(Slot));
        if (string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(PasswordMd5))
            throw new ArgumentException("Password oder PasswordMd5 muss lokal bereitgestellt werden.");
        if (!string.IsNullOrWhiteSpace(PasswordMd5) &&
            (PasswordMd5.Length != 32 || PasswordMd5.Any(c => !Uri.IsHexDigit(c))))
            throw new ArgumentException("PasswordMd5 muss exakt 32 Hex-Zeichen enthalten.");
    }

    public string ResolvePasswordMd5()
    {
        Validate();
        if (!string.IsNullOrWhiteSpace(PasswordMd5)) return PasswordMd5.ToLowerInvariant();
        var hash = MD5.HashData(Encoding.ASCII.GetBytes(Password!));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class FiestaHeadlessProbeResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public FiestaLoadClientStage Stage { get; init; }
    public string WorldHost { get; init; } = string.Empty;
    public int WorldPort { get; init; }
    public string ZoneHost { get; init; } = string.Empty;
    public int ZonePort { get; init; }
    public ushort RandomId { get; init; }
    public int CharacterCount { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public readonly record struct FiestaProtocolSelfTestResult(bool Success, string Detail);

public readonly record struct FiestaHeadlessClientProgress(
    string Username,
    string CharacterName,
    FiestaLoadClientStage Stage,
    string Detail,
    DateTimeOffset TimestampUtc,
    bool Failed = false);

/// <summary>
/// Template of one real, already decrypted NA2016 client -> Zone CH6/1 packet.
/// The template keeps the verified static client-data/checksum region intact and patches only
/// offsets explicitly declared by the template metadata. No guessed 1592-byte layout is hardcoded.
/// </summary>
public sealed class FiestaZoneTransferTemplate
{
    public const string FormatV1 = "NextGen.NA2016.ZoneTransferTemplate.v1";

    public string Format { get; init; } = FormatV1;
    public string PayloadBase64 { get; init; } = string.Empty;
    public int RandomIdOffset { get; init; } = 2;
    public int CharacterNameOffset { get; init; } = 4;
    public int CharacterNameLength { get; init; } = 16;
    public string? SourceSha256 { get; init; }

    public static FiestaZoneTransferTemplate Load(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var template = JsonSerializer.Deserialize<FiestaZoneTransferTemplate>(json)
                       ?? throw new InvalidDataException("Zone-Transfer-Template ist leer oder ungültig.");
        template.Validate();
        return template;
    }

    public byte[] Materialize(ushort randomId, string characterName)
    {
        Validate();
        var payload = Convert.FromBase64String(PayloadBase64);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(RandomIdOffset, 2), randomId);
        FiestaHeadlessLoadClient.WriteFixedAscii(payload.AsSpan(CharacterNameOffset, CharacterNameLength), characterName);

        var packet = FiestaPacket.FromPayload(payload);
        if (packet.Header != 6 || packet.Type != 1)
            throw new InvalidDataException("Materialisiertes Template ist nicht CH6/1.");
        return payload;
    }

    public void Validate()
    {
        if (!string.Equals(Format, FormatV1, StringComparison.Ordinal))
            throw new InvalidDataException($"Unbekanntes Zone-Transfer-Templateformat '{Format}'.");
        byte[] payload;
        try { payload = Convert.FromBase64String(PayloadBase64); }
        catch (FormatException ex) { throw new InvalidDataException("PayloadBase64 ist ungültig.", ex); }
        if (payload.Length < 20)
            throw new InvalidDataException("Zone-Transfer-Template ist zu kurz.");
        var packet = FiestaPacket.FromPayload(payload);
        if (packet.Header != 6 || packet.Type != 1)
            throw new InvalidDataException($"Zone-Transfer-Template enthält Opcode {packet.Header}/{packet.Type} statt CH6/1.");
        if (RandomIdOffset < 2 || RandomIdOffset + 2 > payload.Length)
            throw new InvalidDataException("RandomIdOffset liegt außerhalb des Templates.");
        if (CharacterNameOffset < 2 || CharacterNameLength <= 0 || CharacterNameOffset + CharacterNameLength > payload.Length)
            throw new InvalidDataException("CharacterNameOffset/Length liegt außerhalb des Templates.");
        if (!string.IsNullOrWhiteSpace(SourceSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(payload));
            if (!actual.Equals(SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Zone-Transfer-Template stimmt nicht mit SourceSha256 überein.");
        }
    }
}

internal readonly record struct FiestaPacket(int Header, int Type, byte[] Body)
{
    public static FiestaPacket FromPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) throw new InvalidDataException("Fiesta-Paket ist kürzer als der 2-Byte-Opcode.");
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        var header = opcode >> 10;
        var type = opcode & 1023;
        return new FiestaPacket(header, type, payload[2..].ToArray());
    }
}

internal sealed class FiestaWireConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly FiestaXorCipher _clientToServerCipher;

    private FiestaWireConnection(TcpClient client, NetworkStream stream, FiestaXorCipher cipher)
    {
        _client = client;
        _stream = stream;
        _clientToServerCipher = cipher;
    }

    public static async Task<FiestaWireConnection> ConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token);
            var stream = client.GetStream();
            var handshakePayload = await ReadFrameAsync(stream, timeout, cancellationToken);
            var handshake = FiestaPacket.FromPayload(handshakePayload);
            if (handshake.Header != 2 || handshake.Type != 7 || handshake.Body.Length < 2)
                throw new InvalidDataException($"Erwarteter SH2/7-Handshake fehlt; empfangen wurde {handshake.Header}/{handshake.Type}.");
            var xorPos = BinaryPrimitives.ReadUInt16LittleEndian(handshake.Body.AsSpan(0, 2));
            if (xorPos >= FiestaXorCipher.TableLength)
                throw new InvalidDataException($"Handshake-XOR-Position {xorPos} liegt außerhalb der 499-Byte-Tabelle.");
            return new FiestaWireConnection(client, stream, new FiestaXorCipher(xorPos));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task SendPacketAsync(
        int header,
        int type,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var payload = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), FiestaHeadlessLoadClient.PackOpcode(header, type));
        body.Span.CopyTo(payload.AsSpan(2));
        await SendDecryptedPayloadAsync(payload, cancellationToken);
    }

    public async Task SendDecryptedPayloadAsync(byte[] decryptedPayload, CancellationToken cancellationToken)
    {
        if (decryptedPayload.Length < 2) throw new ArgumentException("Payload ist zu kurz.", nameof(decryptedPayload));
        var encrypted = decryptedPayload.ToArray();
        _clientToServerCipher.TransformInPlace(encrypted);
        var frame = FramePayload(encrypted);
        await _stream.WriteAsync(frame, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async Task<FiestaPacket> ReadPacketAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(_stream, timeout, cancellationToken);
        return FiestaPacket.FromPayload(payload);
    }

    internal static byte[] FramePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(payload), "Fiesta-Payload muss 1..65535 Byte lang sein.");
        if (payload.Length <= byte.MaxValue)
        {
            var frame = new byte[payload.Length + 1];
            frame[0] = (byte)payload.Length;
            payload.CopyTo(frame.AsSpan(1));
            return frame;
        }

        var large = new byte[payload.Length + 3];
        large[0] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(large.AsSpan(1, 2), checked((ushort)payload.Length));
        payload.CopyTo(large.AsSpan(3));
        return large;
    }

    private static async Task<byte[]> ReadFrameAsync(
        NetworkStream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var first = new byte[1];
            await ReadExactlyAsync(stream, first, timeoutCts.Token);
            int length;
            if (first[0] == 0)
            {
                var lengthBytes = new byte[2];
                await ReadExactlyAsync(stream, lengthBytes, timeoutCts.Token);
                length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
            }
            else
            {
                length = first[0];
            }

            if (length <= 0) throw new InvalidDataException("Fiesta-Frame meldet Länge 0.");
            var payload = new byte[length];
            await ReadExactlyAsync(stream, payload, timeoutCts.Token);
            return payload;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Fiesta-Paket wurde nicht innerhalb von {timeout.TotalSeconds:N1}s vollständig empfangen.");
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("Fiesta-Verbindung wurde vom Server geschlossen.");
            offset += read;
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); }
        finally { _client.Dispose(); }
        return ValueTask.CompletedTask;
    }
}

internal sealed class FiestaXorCipher
{
    private const string TableBase64 = "B1lpSpQRlIWMiAXLoJ7NWDo2WxpqFv6935QC+CGWyOme97+9z82yegCfQCL8EfkMLhL7p3QKfXhAHiygLQbLqLl+795J6k4TFhaA9D3CmtSG15QkF/TWZb0/2+ThD1D27HqaDCc9JGbTImicmlIL4PmlCyXagEkN/T530Vaot/QPm+gPUkf1b4MgItsPC7FDhcHLpAsCGd/wi+zbbG1mrUW+iRR+L4kQuJNg2GDe9v5um8oGwXWVM8/AsuDMpc4S9uW1tCbFshhPKl0mG2VN9UXJhBTcfBJLGJzHJOc8ZP/WOizujIFJOWy33L2U4jL33Qr8AgFk7EyUCrFW9cmpNN4POCe8gTAPezgl/ug+KbpVQ79rnx+KSVIYf4r4iCRcT+GoMIeOUB8v0Qy0/Qq83BKF4lLuSlg4q//GPblgZAq0UNVAiRea1YXP7A1+gX/jwwQBIuwnzPo+IaZUyN4Att8nn/YlNAeFv6elpeCDDD1dIECvYKNkVvMFxBx9N5jD6FpuWIWkmmtq9KN7YZsJQB5gSzLZUaT++V1OSvtK1HwzAjPVnc5bqlp82PgF+h8rjHJXUK5sGYnKAfz8KZthEmhjZUYmxFtQqiu+75p5AiN1LCAT/dladiPxC7W4WfmfeuYG6aU6tFC/FliYs5puNu6N6w==";
    private static readonly byte[] Table = Convert.FromBase64String(TableBase64);
    public const int TableLength = 499;
    private int _position;

    static FiestaXorCipher()
    {
        if (Table.Length != TableLength)
            throw new InvalidOperationException($"NA2016 XOR-Tabelle hat {Table.Length} statt {TableLength} Byte.");
    }

    public FiestaXorCipher(int position)
    {
        if (position is < 0 or >= TableLength) throw new ArgumentOutOfRangeException(nameof(position));
        _position = position;
    }

    public void TransformInPlace(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= Table[_position];
            _position++;
            if (_position == TableLength) _position = 0;
        }
    }
}
