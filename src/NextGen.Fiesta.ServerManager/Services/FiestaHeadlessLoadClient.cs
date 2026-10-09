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
    private const int InitialWorldTransferMaxAttempts = 5;
    private const int InitialZoneTransferMaxAttempts = 4;
    private const int ProvisionLoginMaxAttempts = 5;
    private const int RampLoginMaxAttempts = 4;

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

            FiestaEndpointRedirect? zone = null;
            ushort randomId = 0;
            byte characterCount = 0;
            FiestaEndpointRedirect? finalWorld = null;
            FiestaWorldEntryResult? retainedWorldEntry = null;

            // A newly created NA2016 character is persisted by Character.exe before WorldManager's
            // current client-session character cache is guaranteed to be authoritative. The real
            // server logs can otherwise reach Zone with WorldCharName=NULL. Therefore a fresh create
            // is deliberately followed by one clean Login -> World reconnect, then the target slot is
            // resolved from the new SH3/20 CharacterList before CH4/1 is sent.
            for (var worldPass = 0; worldPass < 2 && zone is null; worldPass++)
            {
                FiestaWorldEntryResult? worldResult = null;

                for (var attempt = 1; attempt <= InitialWorldTransferMaxAttempts; attempt++)
                {
                    var world = await LoginAndGetWorldRedirectForRampAsync(
                        options,
                        credential,
                        passwordMd5,
                        clientProfile,
                        cancellationToken,
                        SetStage);
                    finalWorld = world;

                    try
                    {
                        worldResult = await PrepareWorldEntryAsync(
                            world,
                            options,
                            credential,
                            clientProfile,
                            allowCharacterCreate: worldPass == 0,
                            cancellationToken,
                            SetStage);
                        break;
                    }
                    catch (Exception ex)
                        when (IsRetryableWorldHandoffFailure(ex)
                              && ShouldRetryInitialWorldTransfer(credential.Username, attempt))
                    {
                        var delay = GetInitialWorldTransferRetryDelay(
                            credential.Username,
                            attempt);
                        SetStage(
                            FiestaLoadClientStage.WorldConnected,
                            $"Transienter World-Handoff · Retry {attempt + 1}/{InitialWorldTransferMaxAttempts} in {delay.TotalMilliseconds:N0} ms · {ex.Message}");
                        await Task.Delay(delay, cancellationToken);
                    }
                }

                if (worldResult is null)
                    throw new InvalidOperationException("World-Handoff lieferte nach den zulässigen Versuchen kein Ergebnis.");

                randomId = worldResult.RandomId;
                characterCount = worldResult.CharacterCount;

                if (worldResult.CharacterCreated)
                {
                    SetStage(
                        FiestaLoadClientStage.CharacterCreated,
                        $"'{credential.CharacterName}' wurde erstellt; World-Session wird zur autoritativen CharacterList neu aufgebaut.");
                    continue;
                }

                zone = worldResult.Zone
                       ?? throw new InvalidDataException("World-Pass lieferte weder CharacterCreated noch ZoneRedirect.");
                retainedWorldEntry = worldResult;
            }

            if (zone is null || finalWorld is null || retainedWorldEntry?.WorldConnection is null)
            {
                throw new InvalidOperationException(
                    $"Charakter '{credential.CharacterName}' konnte nach der Erstellung nicht aus einer frischen SH3/20-CharacterList ausgewählt werden.");
            }

            // Critical NA2016 invariant: the selected World TCP session must stay alive while
            // Zone verifies the character registration number. Closing World after SH4/3 makes
            // WorldManager lose the selected-character session and Zone fails with Invalid Regnum.
            await using var worldSession = new FiestaWorldConnectionLease(retainedWorldEntry.WorldConnection);
            SetStage(FiestaLoadClientStage.ZoneRedirectReceived);

            FiestaWireConnection? authenticatedZoneConnection = null;
            var zoneTransferPayload = BuildZoneTransferPayload(options, credential, randomId);
            var zoneFailures = new List<string>(InitialZoneTransferMaxAttempts);

            for (var attempt = 1; attempt <= InitialZoneTransferMaxAttempts; attempt++)
            {
                FiestaWireConnection? candidate = null;
                try
                {
                    candidate = await FiestaWireConnection.ConnectAsync(
                        zone.Host,
                        zone.Port,
                        options.StepTimeout,
                        cancellationToken);
                    SetStage(FiestaLoadClientStage.ZoneConnected,
                        $"Zone-TCP verbunden · Versuch {attempt}/{InitialZoneTransferMaxAttempts} · Ziel {zone.Host}:{zone.Port}");

                    await candidate.SendDecryptedPayloadAsync(zoneTransferPayload, cancellationToken);
                    SetStage(FiestaLoadClientStage.ZoneConnected,
                        $"CH6/1 gesendet · Versuch {attempt}/{InitialZoneTransferMaxAttempts} · warte SH6/2");

                    // Real NA2016 Zone login is NC_MAP_LOGIN_REQ (CH6/1) -> initialization cascade ->
                    // NC_MAP_LOGIN_ACK (SH6/2) -> NC_MAP_LOGINCOMPLETE_CMD (CH6/3).
                    // A retry is permitted only BEFORE SH6/2. After ZoneAuthenticated the client
                    // is authoritative and any disconnect remains a hard failure.
                    await WaitForInitialZoneLoginAckAsync(candidate, options.ZoneLoginTimeout, cancellationToken);
                    authenticatedZoneConnection = candidate;
                    candidate = null;
                    SetStage(
                        FiestaLoadClientStage.ZoneAuthenticated,
                        $"NC_MAP_LOGIN_ACK SH6/2 empfangen · Zone-Handoff Versuch {attempt}/{InitialZoneTransferMaxAttempts}");
                    break;
                }
                catch (Exception ex)
                    when (IsTransientInitialZoneTransferFailure(ex)
                          && !cancellationToken.IsCancellationRequested
                          && attempt < InitialZoneTransferMaxAttempts)
                {
                    if (candidate is not null)
                        await candidate.DisposeAsync();

                    zoneFailures.Add($"Versuch {attempt}: {ex.GetType().Name} · {ex.Message}");
                    var delay = GetInitialZoneTransferRetryDelay(credential.Username, attempt);
                    SetStage(
                        FiestaLoadClientStage.ZoneConnected,
                        $"Transienter Zone-Handoff vor SH6/2 · Retry {attempt + 1}/{InitialZoneTransferMaxAttempts} " +
                        $"in {delay.TotalMilliseconds:N0} ms · {ex.Message}");
                    // CH6/1 may consume its registration ticket even when the socket closes
                    // before SH6/2. Replaying that same ticket on another TCP socket is
                    // not an authoritative original-client admission proof.
                    await worldSession.CloseAsync();
                    await Task.Delay(delay, cancellationToken);
                    try
                    {
                        var renewedWorld = await LoginAndGetWorldRedirectForRampAsync(
                            options, credential, passwordMd5, clientProfile,
                            cancellationToken, SetStage);
                        var renewedEntry = await PrepareWorldEntryAsync(
                            renewedWorld, options, credential, clientProfile,
                            allowCharacterCreate: false, cancellationToken, SetStage);
                        if (renewedEntry.CharacterCreated
                            || renewedEntry.Zone is null
                            || renewedEntry.WorldConnection is null)
                        {
                            if (renewedEntry.WorldConnection is not null)
                                await renewedEntry.WorldConnection.DisposeAsync();
                            throw new InvalidDataException(
                                "Erneuerte World-Auswahl lieferte keine gültige Zone-/Session-Registrierung.");
                        }

                        worldSession.Set(renewedEntry.WorldConnection);
                        finalWorld = renewedWorld;
                        zone = renewedEntry.Zone;
                        randomId = renewedEntry.RandomId;
                        characterCount = renewedEntry.CharacterCount;
                        zoneTransferPayload = BuildZoneTransferPayload(options, credential, randomId);
                        SetStage(FiestaLoadClientStage.ZoneRedirectReceived,
                            $"Frischer Login→World→SH3/20→SH4/3 nach Zone-Abbruch · " +
                            $"Versuch {attempt + 1}/{InitialZoneTransferMaxAttempts} · keine Charaktererstellung");
                    }
                    catch (Exception handoffError) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException(
                            $"Zone-Versuch {attempt} vor SH6/2 abgebrochen ({ex.Message}); " +
                            $"frischer World-Handoff für Versuch {attempt + 1} fehlgeschlagen: {handoffError.Message}",
                            handoffError);
                    }
                }
                catch (Exception ex)
                {
                    if (candidate is not null)
                        await candidate.DisposeAsync();
                    if (cancellationToken.IsCancellationRequested)
                        throw;

                    zoneFailures.Add($"Versuch {attempt}: {ex.GetType().Name} · {ex.Message}");
                    throw new InvalidOperationException(
                        $"Zone-Handoff scheiterte vor SH6/2 bei Versuch {attempt}/{InitialZoneTransferMaxAttempts}. " +
                        $"Verlauf: {string.Join(" | ", zoneFailures)}",
                        ex);
                }
            }

            await using var zoneConnection = authenticatedZoneConnection
                ?? throw new InvalidOperationException(
                    $"Zone-Handoff erreichte SH6/2 nach {InitialZoneTransferMaxAttempts} Versuch(en) nicht.");

            await zoneConnection.SendPacketAsync(
                6,
                3,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken);
            SetStage(
                FiestaLoadClientStage.ClientReady,
                "NC_MAP_LOGINCOMPLETE_CMD CH6/3 gesendet");

            var holdFor = options.HoldDuration;
            if (holdFor > TimeSpan.Zero)
            {
                SetStage(FiestaLoadClientStage.Holding);
                await HoldSessionsAsync(
                    worldSession.Connection,
                    zoneConnection,
                    holdFor,
                    heartbeatDetail => progress?.Invoke(new FiestaHeadlessClientProgress(
                        credential.Username,
                        credential.CharacterName,
                        FiestaLoadClientStage.Holding,
                        heartbeatDetail,
                        DateTimeOffset.UtcNow)),
                    cancellationToken);
            }

            SetStage(FiestaLoadClientStage.Completed);
            return new FiestaHeadlessProbeResult
            {
                Success = true,
                Stage = stage,
                WorldHost = finalWorld.Host,
                WorldPort = finalWorld.Port,
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

    public async Task<FiestaHeadlessProbeResult> ProvisionIdentityAsync(
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

        SetStage(FiestaLoadClientStage.None, "Identity-Provision Start");
        try
        {
            var passwordMd5 = credential.ResolvePasswordMd5();
            var clientProfile = string.IsNullOrWhiteSpace(options.ClientCaptureProfilePath)
                ? null
                : FiestaCapturedClientProfile.Load(options.ClientCaptureProfilePath);

            FiestaEndpointRedirect? zone = null;
            FiestaEndpointRedirect? finalWorld = null;
            ushort randomId = 0;
            byte characterCount = 0;
            var createdThisRun = false;

            // Provisioning deliberately stops after an authoritative World character selection
            // yielded SH4/3 ZoneRedirect. No Zone session is kept alive, so this phase cannot be
            // mistaken for the later ShinePlayer capacity measurement.
            for (var worldPass = 0; worldPass < 2 && zone is null; worldPass++)
            {
                FiestaWorldEntryResult? worldResult = null;

                for (var attempt = 1; attempt <= InitialWorldTransferMaxAttempts; attempt++)
                {
                    var world = await LoginAndGetWorldRedirectForProvisioningAsync(
                        options,
                        credential,
                        passwordMd5,
                        clientProfile,
                        cancellationToken,
                        SetStage);
                    finalWorld = world;

                    try
                    {
                        worldResult = await PrepareWorldEntryAsync(
                            world,
                            options,
                            credential,
                            clientProfile,
                            allowCharacterCreate: worldPass == 0,
                            cancellationToken,
                            SetStage);
                        break;
                    }
                    catch (Exception ex)
                        when (IsRetryableWorldHandoffFailure(ex)
                              && ShouldRetryInitialWorldTransfer(credential.Username, attempt))
                    {
                        var delay = GetInitialWorldTransferRetryDelay(credential.Username, attempt);
                        SetStage(
                            FiestaLoadClientStage.WorldConnected,
                            $"Provisioning World-Retry {attempt + 1}/{InitialWorldTransferMaxAttempts} in {delay.TotalMilliseconds:N0} ms · {ex.Message}");
                        await Task.Delay(delay, cancellationToken);
                    }
                }

                if (worldResult is null)
                    throw new InvalidOperationException(
                        "Identity-Provisioning lieferte nach den zulässigen World-Versuchen kein Ergebnis.");

                randomId = worldResult.RandomId;
                characterCount = worldResult.CharacterCount;

                if (worldResult.CharacterCreated)
                {
                    createdThisRun = true;
                    SetStage(
                        FiestaLoadClientStage.CharacterCreated,
                        $"'{credential.CharacterName}' wurde erstellt; warte kurz auf Persistierung vor dem autoritativen Relogin.");
                    await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
                    continue;
                }

                zone = worldResult.Zone
                       ?? throw new InvalidDataException(
                           "Identity-Provisioning erhielt nach Charakterauswahl keinen SH4/3 ZoneRedirect.");

                if (worldResult.WorldConnection is not null)
                    await worldResult.WorldConnection.DisposeAsync();

                SetStage(
                    FiestaLoadClientStage.ZoneRedirectReceived,
                    $"IDENTITY READY · SH3/20 enthält '{credential.CharacterName}' und SH4/3 ZoneRedirect wurde bestätigt.");
            }

            if (zone is null || finalWorld is null)
                throw new InvalidOperationException(
                    $"Identity-Provisioning für '{credential.Username}/{credential.CharacterName}' blieb ohne gültigen ZoneRedirect.");

            return new FiestaHeadlessProbeResult
            {
                Success = true,
                Stage = FiestaLoadClientStage.ZoneRedirectReceived,
                WorldHost = finalWorld.Host,
                WorldPort = finalWorld.Port,
                ZoneHost = zone.Host,
                ZonePort = zone.Port,
                RandomId = randomId,
                CharacterCount = characterCount,
                Detail =
                    $"IDENTITY PROVISION PASS · {credential.Username}/{credential.CharacterName} · " +
                    $"{(createdThisRun ? "Account/Charakter neu bzw. frisch persistiert" : "bereits vorhanden")} · " +
                    $"World-Auswahl + SH4/3 bestätigt."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.Invoke(new FiestaHeadlessClientProgress(
                credential.Username,
                credential.CharacterName,
                stage,
                "Identity-Provisioning abgebrochen",
                DateTimeOffset.UtcNow,
                Failed: true));
            return new FiestaHeadlessProbeResult
            {
                Success = false,
                Cancelled = true,
                Stage = stage,
                Detail = $"Identity-Provisioning bei Stufe {stage} abgebrochen."
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
                Detail = $"Identity-Provisioning bei Stufe {stage} fehlgeschlagen: {ex.Message}"
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

            var listBody = new byte[3 + 2 * 130];
            BinaryPrimitives.WriteUInt16LittleEndian(listBody.AsSpan(0, 2), 6000);
            listBody[2] = 2;
            BinaryPrimitives.WriteUInt32LittleEndian(listBody.AsSpan(3, 4), 2);
            WriteFixedAscii(listBody.AsSpan(7, 20), "FirstChar");
            BinaryPrimitives.WriteUInt32LittleEndian(listBody.AsSpan(133, 4), 8);
            WriteFixedAscii(listBody.AsSpan(137, 20), "NGCap01");
            var list = ParseCharacterList(listBody);
            if (list.RandomId != 6000
                || list.CharacterCount != 2
                || list.Characters.Count != 2
                || list.Characters[1].Slot != 1
                || list.Characters[1].CharacterNumber != 8
                || list.Characters[1].CharacterName != "NGCap01"
                || GetNextCharacterCreateSlot(list) != 2)
            {
                throw new InvalidDataException("SH3/20 CharacterList/Slot-Auswertung ist falsch.");
            }

            var createAck = new byte[131];
            createAck[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(createAck.AsSpan(1, 4), 9);
            WriteFixedAscii(createAck.AsSpan(5, 20), "NGL000001");
            ValidateCharacterCreateAck(createAck, "NGL000001");

            if (IsPostReadyRemoteCloseFatal("World")
                || !IsPostReadyRemoteCloseFatal("Zone"))
            {
                throw new InvalidDataException(
                    "Post-ClientReady Remote-Close-Policy muss World tolerieren und Zone blockieren.");
            }

            if (!ShouldRetryInitialWorldTransfer("r_ngl000002", 1)
                || ShouldRetryInitialWorldTransfer("r_ngl000002", InitialWorldTransferMaxAttempts)
                || ShouldRetryInitialWorldTransfer("ngl000002", 1))
            {
                throw new InvalidDataException(
                    "Initial-World-Retry darf nur für r_-Accounts und nur begrenzt aktiv sein.");
            }

            if (!IsTransientInitialWorldTransportFailure(new TimeoutException())
                || !IsTransientInitialWorldTransportFailure(new EndOfStreamException())
                || IsTransientInitialWorldTransportFailure(new InvalidDataException()))
            {
                throw new InvalidDataException(
                    "Initial-World-Transportklassifizierung ist zu breit oder zu eng.");
            }

            if (!IsRetryableWorldHandoffFailure(
                    new FiestaWorldCharacterLoginRejectedException(1, "test"))
                || !IsRetryableWorldHandoffFailure(
                    new FiestaWorldCharacterNotVisibleException("test"))
                || IsRetryableWorldHandoffFailure(new InvalidOperationException("test")))
            {
                throw new InvalidDataException(
                    "World-Character-/Persistierungs-Retryklassifizierung ist zu breit oder zu eng.");
            }

            if (!IsTransientInitialZoneTransferFailure(new EndOfStreamException())
                || !IsTransientInitialZoneTransferFailure(new TimeoutException())
                || !IsTransientInitialZoneTransferFailure(new InvalidDataException("Fiesta-Frame meldet Länge 0."))
                || !IsTransientInitialZoneTransferFailure(new InvalidOperationException("World/Zone meldete SH4/2 ConnectError."))
                || IsTransientInitialZoneTransferFailure(new InvalidOperationException("SH6/2 bereits empfangen; späterer Fehler")))
            {
                throw new InvalidDataException(
                    "Initial-Zone-Retryklassifizierung ist zu breit oder zu eng.");
            }

            var zoneRetry1 = GetInitialZoneTransferRetryDelay("r_ngl000240", 1);
            var zoneRetry2 = GetInitialZoneTransferRetryDelay("r_ngl000240", 2);
            if (zoneRetry1 < TimeSpan.FromMilliseconds(750)
                || zoneRetry1 > TimeSpan.FromMilliseconds(1250)
                || zoneRetry2 <= zoneRetry1)
            {
                throw new InvalidDataException(
                    $"Initial-Zone-Retry-Backoff ist ungültig: {zoneRetry1.TotalMilliseconds:N0}/{zoneRetry2.TotalMilliseconds:N0} ms.");
            }

            if (!IsIgnorablePostReadyWorldFrameTermination(
                    new InvalidDataException("Fiesta-Frame meldet Länge 0."))
                || IsIgnorablePostReadyWorldFrameTermination(
                    new InvalidDataException("Fiesta-Frame ist abgeschnitten.")))
            {
                throw new InvalidDataException(
                    "PostReady-World-Framingklassifizierung ist zu breit oder zu eng.");
            }

            if (!IsTransientProvisioningLoginFailure(new EndOfStreamException())
                || !IsTransientProvisioningLoginFailure(new OperationCanceledException())
                || !IsTransientProvisioningLoginFailure(new InvalidDataException("Fiesta-Frame meldet Länge 0."))
                || IsTransientProvisioningLoginFailure(new InvalidOperationException("Login/World meldete SH3/9 Error."))
                || IsTransientProvisioningLoginFailure(new InvalidDataException("Falsches Handshake-Opcode")))
            {
                throw new InvalidDataException("Provisioning-Login-Retryklassifizierung ist unzulässig breit oder eng.");
            }

            if (RampLoginMaxAttempts != 4
                || !IsTransientRampLoginFailure(new EndOfStreamException())
                || !IsTransientRampLoginFailure(new TimeoutException())
                || !IsTransientRampLoginFailure(new InvalidDataException("Fiesta-Frame meldet Länge 0."))
                || IsTransientRampLoginFailure(new InvalidOperationException("Login/World meldete SH3/9 Error."))
                || IsTransientRampLoginFailure(new FiestaWorldCharacterLoginRejectedException(7, "SH4/2")))
            {
                throw new InvalidDataException(
                    "Ramp-Login-Transport-Retries müssen begrenzt sein und dürfen keine explizite World-Ablehnung verdecken.");
            }

            var provisioningLoginRetry1 = GetProvisionLoginRetryDelay("r_ngl000002", 1);
            var provisioningLoginRetry2 = GetProvisionLoginRetryDelay("r_ngl000002", 2);
            if (provisioningLoginRetry1 < TimeSpan.FromMilliseconds(750)
                || provisioningLoginRetry1 > TimeSpan.FromMilliseconds(1250)
                || provisioningLoginRetry2 <= provisioningLoginRetry1)
            {
                throw new InvalidDataException("Provisioning-Login-Retry-Backoff ist ungültig.");
            }

            if (!IsTransientInitialZoneTransferFailure(
                    new IOException("Zone-Verbindung während CH6/1→SH6/2 unterbrochen"))
                || IsTransientInitialZoneTransferFailure(
                    new InvalidOperationException("Zone meldete SH3/9 Error")))
            {
                throw new InvalidDataException("Zone-Handshake-Diagnose darf explizite Serverfehler nicht als Retry einstufen.");
            }

            var earlyWorldSh54 = DescribeWorldSh54(
                new FiestaPacket(5, 4, new byte[] { 0x81, 0x01 }),
                "vor SH3/20 CharacterList (CH5/1 wurde nicht gesendet)");
            if (!earlyWorldSh54.Contains("UInt16LE=385", StringComparison.Ordinal)
                || !earlyWorldSh54.Contains("BodyHex=8101", StringComparison.Ordinal)
                || !earlyWorldSh54.Contains("CH5/1 wurde nicht gesendet", StringComparison.Ordinal)
                || earlyWorldSh54.Contains("CharacterCreationError", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "SH5/4-World-Diagnose muss die Rohbytes und die tatsächliche Protokollphase liefern.");
            }

            var serverHeartbeatRequest = BuildPacketPayload(2, 4, ReadOnlySpan<byte>.Empty);
            var clientHeartbeatAck = BuildPacketPayload(2, 5, ReadOnlySpan<byte>.Empty);
            if (serverHeartbeatRequest.Length != 2
                || clientHeartbeatAck.Length != 2
                || BinaryPrimitives.ReadUInt16LittleEndian(serverHeartbeatRequest) != PackOpcode(2, 4)
                || BinaryPrimitives.ReadUInt16LittleEndian(clientHeartbeatAck) != PackOpcode(2, 5))
            {
                throw new InvalidDataException(
                    "Capture-basierter Zone-Heartbeat muss SH2/4 → CH2/5 ohne Body verwenden.");
            }

            var retry1 = GetInitialWorldTransferRetryDelay("r_ngl000002", 1);
            var retry2 = GetInitialWorldTransferRetryDelay("r_ngl000002", 2);
            if (retry1 < TimeSpan.FromMilliseconds(500)
                || retry1 > TimeSpan.FromMilliseconds(900)
                || retry2 <= retry1)
            {
                throw new InvalidDataException(
                    $"Initial-World-Retry-Backoff ist ungültig: {retry1.TotalMilliseconds:N0}/{retry2.TotalMilliseconds:N0} ms.");
            }

            return new FiestaProtocolSelfTestResult(
                true,
                "NA2016 PROTOCOL SELFTEST: PASS · Login-Reihenfolge · r_-World-Handoff bounded retry+jitter vor SH3/20 sowie bei NC_CHAR_LOGINFAIL_ACK SH4/2 nach Charakterauswahl · phasengenaue SH5/4-BodyHex-Diagnose · getrennte Provisioning-Login-Transport-Retries · SH3/20 Slot/CharNo · CH2/13 GameTime · SH5/6 Create-Ack · Create→Relogin · Zone: bounded retry nur vor SH6/2 bei RemoteClose/Timeout/SH4/2/Frame0 · CH6/1→SH6/2→CH6/3 · Holding capture-basiert: servergetriebenes SH2/4→CH2/5; kein aktives CH2/4 · PostReady: World-Close toleriert, Zone-Close blockiert · 64-Byte World-Transfermaterial.");
        }
        catch (Exception ex)
        {
            return new FiestaProtocolSelfTestResult(false, "NA2016 PROTOCOL SELFTEST: FAIL · " + ex.Message);
        }
    }

    private static async Task<FiestaEndpointRedirect> LoginAndGetWorldRedirectAsync(
        FiestaHeadlessProbeOptions options,
        FiestaLoadClientCredential credential,
        string passwordMd5,
        FiestaCapturedClientProfile? clientProfile,
        CancellationToken cancellationToken,
        Action<FiestaLoadClientStage, string> setStage)
    {
        await using var login = await FiestaWireConnection.ConnectAsync(
            options.LoginHost,
            options.LoginPort,
            options.StepTimeout,
            cancellationToken);
        setStage(FiestaLoadClientStage.LoginConnected, string.Empty);

        await login.SendPacketAsync(3, 101, BuildVersionBody(options, clientProfile), cancellationToken);
        await WaitForAsync(login, 3, 103, options.StepTimeout, cancellationToken);
        setStage(FiestaLoadClientStage.VersionAccepted, string.Empty);

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
        setStage(FiestaLoadClientStage.LoginAuthenticated, string.Empty);

        await login.SendPacketAsync(3, 11, new[] { options.WorldId }, cancellationToken);
        var worldRedirect = await WaitForAsync(login, 3, 12, options.StepTimeout, cancellationToken);
        var world = ParseWorldRedirect(worldRedirect.Body);
        setStage(FiestaLoadClientStage.WorldRedirectReceived, string.Empty);
        return world;
    }

    // The ramp measures simultaneous Zone sessions, not whether an unrelated short-lived
    // Login TCP accept occasionally closes before WorldRedirect. Retry ONLY transport
    // failures before SH3/12. Every retry is observable in the ramp trace; explicit
    // Login/World protocol rejections remain hard failures.
    private static async Task<FiestaEndpointRedirect> LoginAndGetWorldRedirectForRampAsync(
        FiestaHeadlessProbeOptions options,
        FiestaLoadClientCredential credential,
        string passwordMd5,
        FiestaCapturedClientProfile? clientProfile,
        CancellationToken cancellationToken,
        Action<FiestaLoadClientStage, string> setStage)
    {
        for (var attempt = 1; attempt <= RampLoginMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await LoginAndGetWorldRedirectAsync(
                    options, credential, passwordMd5, clientProfile, cancellationToken, setStage);
            }
            catch (Exception ex) when (IsTransientRampLoginFailure(ex)
                                       && !cancellationToken.IsCancellationRequested)
            {
                if (attempt == RampLoginMaxAttempts)
                {
                    throw new IOException(
                        $"Ramp Login-Transport nach {attempt}/{RampLoginMaxAttempts} Versuchen " +
                        $"fehlgeschlagen: {ex.GetType().Name}: {ex.Message}", ex);
                }

                var delay = GetProvisionLoginRetryDelay(credential.Username, attempt);
                setStage(FiestaLoadClientStage.None,
                    $"RAMP_LOGIN_RETRY · Versuch {attempt}/{RampLoginMaxAttempts} · " +
                    $"{ex.GetType().Name}: {ex.Message} · Warte {delay.TotalMilliseconds:N0} ms " +
                    "vor vollständigem frischem Login");
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Ramp Login-Versuche unerwartet beendet.");
    }

    private static bool IsTransientRampLoginFailure(Exception ex)
        => IsTransientInitialWorldTransportFailure(ex)
           || ex is OperationCanceledException
           || (ex is InvalidDataException data
               && data.Message.Contains("Fiesta-Frame meldet Länge 0", StringComparison.OrdinalIgnoreCase));

    // Account auto-registration is database-backed. During isolated provisioning the
    // stock login server may close an initial handshake under short-lived pressure.
    // Retry transport failures only; do not mask SH3/9 or other explicit rejections.
    // This intentionally does NOT change the load benchmark's connection behavior.
    private static async Task<FiestaEndpointRedirect> LoginAndGetWorldRedirectForProvisioningAsync(
        FiestaHeadlessProbeOptions options,
        FiestaLoadClientCredential credential,
        string passwordMd5,
        FiestaCapturedClientProfile? clientProfile,
        CancellationToken cancellationToken,
        Action<FiestaLoadClientStage, string> setStage)
    {
        for (var attempt = 1; attempt <= ProvisionLoginMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await LoginAndGetWorldRedirectAsync(
                    options, credential, passwordMd5, clientProfile, cancellationToken, setStage);
            }
            catch (Exception ex) when (IsTransientProvisioningLoginFailure(ex)
                                       && !cancellationToken.IsCancellationRequested)
            {
                if (attempt == ProvisionLoginMaxAttempts)
                    throw new IOException(
                        $"Provisioning Login-Transport nach {attempt}/{ProvisionLoginMaxAttempts} Versuchen fehlgeschlagen: {ex.Message}",
                        ex);

                var delay = GetProvisionLoginRetryDelay(credential.Username, attempt);
                setStage(FiestaLoadClientStage.None,
                    $"Provisioning Login-Transport: Versuch {attempt}/{ProvisionLoginMaxAttempts} fehlgeschlagen " +
                    $"({ex.GetType().Name}: {ex.Message}); erneuter Login in {delay.TotalMilliseconds:N0} ms.");
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Provisioning Login-Versuche unerwartet beendet.");
    }

    private static bool IsTransientProvisioningLoginFailure(Exception ex)
        => IsTransientInitialWorldTransportFailure(ex)
           || ex is OperationCanceledException
           || (ex is InvalidDataException data
               && data.Message.Contains("Fiesta-Frame meldet Länge 0", StringComparison.OrdinalIgnoreCase));

    private static TimeSpan GetProvisionLoginRetryDelay(string username, int attempt)
    {
        if (attempt is < 1 or >= ProvisionLoginMaxAttempts)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        var jitter = username.Aggregate(0, (sum, ch) => (sum + ch) % 5);
        return TimeSpan.FromMilliseconds(750 * attempt + jitter * 125);
    }

    private static bool IsTransientInitialWorldTransportFailure(Exception ex)
        => ex is TimeoutException
           or EndOfStreamException
           or IOException
           or SocketException;

    private static bool IsRetryableWorldHandoffFailure(Exception ex)
        => ex is FiestaInitialWorldTransferRejectedException
           or FiestaWorldCharacterLoginRejectedException
           or FiestaWorldCharacterNotVisibleException;

    private static bool IsTransientInitialZoneTransferFailure(Exception ex)
    {
        if (ex is TimeoutException or EndOfStreamException or IOException or SocketException)
            return true;

        if (ex is InvalidDataException data
            && data.Message.Contains("Fiesta-Frame meldet Länge 0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex is InvalidOperationException operation
               && operation.Message.Contains("SH4/2 ConnectError", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetInitialZoneTransferRetryDelay(string username, int attempt)
    {
        if (attempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempt));

        var jitterBucket = username.Aggregate(0, (sum, ch) => (sum + ch) % 5);
        return TimeSpan.FromMilliseconds(
            750
            + ((attempt - 1) * 750)
            + (jitterBucket * 125));
    }

    private static bool ShouldRetryInitialWorldTransfer(string username, int attempt)
        => username.StartsWith("r_", StringComparison.OrdinalIgnoreCase)
           && attempt < InitialWorldTransferMaxAttempts;

    private static TimeSpan GetInitialWorldTransferRetryDelay(string username, int attempt)
    {
        if (attempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempt));

        // Deterministic per-account jitter prevents nine freshly auto-registered clients from
        // retrying the World handoff in lockstep after the same SH4/2 response.
        var jitterBucket = username.Aggregate(0, (sum, ch) => (sum + ch) % 5);
        var milliseconds =
            500
            + ((attempt - 1) * 500)
            + (jitterBucket * 100);
        return TimeSpan.FromMilliseconds(milliseconds);
    }


    // SH5/4 may arrive even before CH5/1 CharacterCreate was sent. Keep the exact
    // wire evidence and protocol phase instead of incorrectly claiming creation failed.
    // UInt16LE is an observed body field, NOT a verified semantic error-code mapping.
    private static string DescribeWorldSh54(FiestaPacket packet, string phase)
    {
        const int maxBodyBytes = 48;
        var rawCode = packet.Body.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(packet.Body.AsSpan(0, 2)).ToString()
            : "<fehlt>";
        var bodyHex = packet.Body.Length == 0
            ? "<leer>"
            : Convert.ToHexString(packet.Body.AsSpan(0, Math.Min(packet.Body.Length, maxBodyBytes)));
        if (packet.Body.Length > maxBodyBytes)
            bodyHex += "...";

        return $"World meldete SH5/4 {phase} (UInt16LE={rawCode}, BodyLength={packet.Body.Length}, BodyHex={bodyHex}). " +
               "Die Bedeutung des Feldes ist ohne korrelierte World-/Character-Serverlogs nicht bestätigt.";
    }

    private static async Task<FiestaPacket> WaitForInitialWorldCharacterListAsync(
        FiestaWireConnection connection,
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
                await connection.SendPacketAsync(
                    2,
                    5,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken);
                continue;
            }

            if (packet.Header == 3 && packet.Type == 9)
                throw new InvalidOperationException("Login/World meldete SH3/9 Error.");

            if (packet.Header == 4 && packet.Type == 2)
            {
                var body = packet.Body.Length == 0
                    ? "<leer>"
                    : Convert.ToHexString(packet.Body);
                throw new FiestaInitialWorldTransferRejectedException(
                    $"World meldete SH4/2 vor SH3/20 CharacterList (Body={body}).");
            }

            if (packet.Header == 5 && packet.Type == 4)
                throw new InvalidOperationException(DescribeWorldSh54(
                    packet, "vor SH3/20 CharacterList (CH5/1 wurde nicht gesendet)"));

            if (packet.Header == 3 && packet.Type == 20)
                return packet;
        }

        throw new TimeoutException(
            $"SH3/20 CharacterList wurde innerhalb von {timeout.TotalSeconds:N0}s nicht empfangen.");
    }

    private static async Task<FiestaWorldEntryResult> PrepareWorldEntryAsync(
        FiestaEndpointRedirect world,
        FiestaHeadlessProbeOptions options,
        FiestaLoadClientCredential credential,
        FiestaCapturedClientProfile? clientProfile,
        bool allowCharacterCreate,
        CancellationToken cancellationToken,
        Action<FiestaLoadClientStage, string> setStage)
    {
        FiestaWireConnection? worldConnection = null;
        try
        {
            FiestaPacket characterListPacket;
            try
            {
                worldConnection = await FiestaWireConnection.ConnectAsync(
                    world.Host,
                    world.Port,
                    options.StepTimeout,
                    cancellationToken);
                setStage(FiestaLoadClientStage.WorldConnected, string.Empty);

                await worldConnection.SendPacketAsync(
                    3,
                    15,
                    BuildWorldTransferBody(options, clientProfile, credential.Username, world.TransferMaterial),
                    cancellationToken);

                characterListPacket = await WaitForInitialWorldCharacterListAsync(
                    worldConnection,
                    options.StepTimeout,
                    cancellationToken);
            }
            catch (FiestaInitialWorldTransferRejectedException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientInitialWorldTransportFailure(ex))
            {
                throw new FiestaInitialWorldTransferRejectedException(
                    $"Initialer World-Handoff wurde vor SH3/20 unterbrochen: {ex.Message}",
                    ex);
            }

            var characterList = ParseCharacterList(characterListPacket.Body);

            // Real capture sends CH2/13 GameTime immediately after SH3/20 and receives SH2/14
            // before create/select. Keep the original state transition instead of skipping it.
            await worldConnection.SendPacketAsync(2, 13, ReadOnlyMemory<byte>.Empty, cancellationToken);
            await WaitForAsync(worldConnection, 2, 14, options.StepTimeout, cancellationToken);

            var target = characterList.Characters.FirstOrDefault(x =>
                x.CharacterName.Equals(credential.CharacterName, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                await worldConnection.SendPacketAsync(4, 1, new[] { target.Slot }, cancellationToken);
                var zoneRedirect = await WaitForZoneRedirectAsync(
                    worldConnection,
                    clientProfile,
                    options.StepTimeout,
                    cancellationToken);

                // Ownership of this live connection moves to the caller. It MUST remain open
                // through Zone CH6/1 registration-number verification and the load-test hold.
                var retainedConnection = worldConnection;
                worldConnection = null;
                return new FiestaWorldEntryResult(
                    ParseZoneRedirect(zoneRedirect.Body),
                    characterList.RandomId,
                    characterList.CharacterCount,
                    CharacterCreated: false,
                    retainedConnection);
            }

            if (!allowCharacterCreate)
            {
                throw new FiestaWorldCharacterNotVisibleException(
                    $"Charakter '{credential.CharacterName}' ist nach der Erstellung noch nicht in der autoritativen SH3/20-Liste sichtbar.");
            }

            if (!credential.CreateCharacterIfMissing)
            {
                throw new InvalidOperationException(
                    $"Charakter '{credential.CharacterName}' ist in der autoritativen SH3/20-Liste nicht vorhanden und Auto-Create ist für dieses Manifest deaktiviert.");
            }

            if (string.IsNullOrWhiteSpace(options.CharacterCreateTemplatePath))
            {
                throw new InvalidOperationException(
                    "Auto-Create benötigt ein capture-basiertes CH5/1 CharacterCreate-Template.");
            }

            var createSlot = GetNextCharacterCreateSlot(characterList);
            var createTemplate = FiestaCharacterCreateTemplate.Load(options.CharacterCreateTemplatePath);
            var createPayload = createTemplate.Materialize(createSlot, credential.CharacterName);
            await worldConnection.SendDecryptedPayloadAsync(createPayload, cancellationToken);

            var createAck = await WaitForAsync(
                worldConnection,
                5,
                6,
                options.StepTimeout,
                cancellationToken);
            ValidateCharacterCreateAck(createAck.Body, credential.CharacterName);

            // This create-only pass is intentionally closed. The caller performs one clean relogin
            // so the next World session receives the persisted character in SH3/20.
            await worldConnection.DisposeAsync();
            worldConnection = null;

            return new FiestaWorldEntryResult(
                Zone: null,
                RandomId: characterList.RandomId,
                CharacterCount: checked((byte)(characterList.CharacterCount + 1)),
                CharacterCreated: true,
                WorldConnection: null);
        }
        catch
        {
            if (worldConnection is not null)
                await worldConnection.DisposeAsync();
            throw;
        }
    }

    private static FiestaCharacterListSnapshot ParseCharacterList(ReadOnlySpan<byte> body)
    {
        const int recordSize = 130;
        const int nameOffset = 4;
        const int nameLength = 20;

        if (body.Length < 3)
            throw new InvalidDataException("SH3/20 CharacterList ist kürzer als 3 Byte.");

        var randomId = BinaryPrimitives.ReadUInt16LittleEndian(body[..2]);
        var count = body[2];
        var requiredLength = 3 + count * recordSize;
        if (body.Length < requiredLength)
        {
            throw new InvalidDataException(
                $"SH3/20 CharacterList meldet {count} Charaktere, enthält aber nur {body.Length} statt mindestens {requiredLength} Byte.");
        }

        var characters = new List<FiestaCharacterListEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var record = body.Slice(3 + index * recordSize, recordSize);
            var characterNumber = BinaryPrimitives.ReadUInt32LittleEndian(record[..4]);
            var characterName = ReadFixedAscii(record.Slice(nameOffset, nameLength));
            if (characterNumber == 0 || string.IsNullOrWhiteSpace(characterName))
            {
                throw new InvalidDataException(
                    $"SH3/20 CharacterList-Eintrag {index} enthält keinen gültigen CharNo/CharName.");
            }

            characters.Add(new FiestaCharacterListEntry(
                checked((byte)index),
                characterNumber,
                characterName));
        }

        return new FiestaCharacterListSnapshot(randomId, count, characters);
    }

    private static byte GetNextCharacterCreateSlot(FiestaCharacterListSnapshot list)
    {
        // The original capture had two existing records and created the third character with
        // CH5/1 slot=2. SH3/20 is compact and ordered by slot, so appending at CharacterCount
        // reproduces the observed client behavior while avoiding collisions with occupied slots.
        if (list.CharacterCount >= 10)
            throw new InvalidOperationException("Der Testaccount hat bereits 10 Charaktere; kein Auto-Create-Slot ist frei.");
        return list.CharacterCount;
    }

    private static void ValidateCharacterCreateAck(ReadOnlySpan<byte> body, string expectedCharacterName)
    {
        // Real SH5/6: byte result + 130-byte character record.
        if (body.Length < 131)
            throw new InvalidDataException($"SH5/6 CharacterCreate-Ack ist zu kurz ({body.Length} Byte).");
        if (body[0] != 1)
            throw new InvalidDataException($"SH5/6 CharacterCreate-Ack meldet Result={body[0]} statt Erfolg=1.");

        var characterNumber = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(1, 4));
        var characterName = ReadFixedAscii(body.Slice(5, 20));
        if (characterNumber == 0
            || !characterName.Equals(expectedCharacterName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SH5/6 bestätigte CharNo={characterNumber}, Name='{characterName}' statt '{expectedCharacterName}'.");
        }
    }

    private static bool IsPostReadyRemoteCloseFatal(string role)
        => role.Equals("Zone", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnorablePostReadyWorldFrameTermination(InvalidDataException ex)
        => ex.Message.Contains("Fiesta-Frame meldet Länge 0", StringComparison.OrdinalIgnoreCase);

    private static async Task HoldSessionsAsync(
        FiestaWireConnection worldConnection,
        FiestaWireConnection zoneConnection,
        TimeSpan duration,
        Action<string>? zoneHeartbeatProgress,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
            return;

        using var holdCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        holdCts.CancelAfter(duration);

        var worldPump = PumpSessionAsync(
            worldConnection,
            "World",
            allowRemoteClose: true,
            heartbeatProgress: null,
            holdCts.Token);
        var zonePump = PumpSessionAsync(
            zoneConnection,
            "Zone",
            allowRemoteClose: false,
            zoneHeartbeatProgress,
            holdCts.Token);

        try
        {
            await Task.WhenAll(worldPump, zonePump);
        }
        catch (OperationCanceledException) when (holdCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Expected end of the requested hold interval.
        }
    }

    private static async Task PumpSessionAsync(
        FiestaWireConnection connection,
        string role,
        bool allowRemoteClose,
        Action<string>? heartbeatProgress,
        CancellationToken cancellationToken)
    {
        var serverHeartbeatRequests = 0;
        DateTimeOffset? lastServerHeartbeatUtc = null;
        var sessionStartedUtc = DateTimeOffset.UtcNow;
        var recentServerPackets = new Queue<(DateTimeOffset At, string Summary)>();

        void RememberServerPacket(FiestaPacket packet)
        {
            var previewLength = Math.Min(packet.Body.Length, 8);
            var preview = previewLength == 0
                ? "-"
                : Convert.ToHexString(packet.Body.AsSpan(0, previewLength));
            recentServerPackets.Enqueue((
                DateTimeOffset.UtcNow,
                $"SH{packet.Header}/{packet.Type} body={packet.Body.Length:N0} preview={preview}"));
            while (recentServerPackets.Count > 6)
                recentServerPackets.Dequeue();
        }

        string HoldingTelemetry()
        {
            var now = DateTimeOffset.UtcNow;
            var heartbeatAge = lastServerHeartbeatUtc is null
                ? "noch kein SH2/4 vom Server empfangen"
                : $"letztes SH2/4 vor {Math.Max(0, (now - lastServerHeartbeatUtc.Value).TotalSeconds):N1}s";
            var recent = recentServerPackets.Count == 0
                ? "keine sonstigen Serverpakete"
                : string.Join(", ", recentServerPackets.Select(x =>
                    $"{x.Summary} vor {Math.Max(0, (now - x.At).TotalSeconds):N1}s"));

            return
                $"Sessionalter {Math.Max(0, (now - sessionStartedUtc).TotalSeconds):N1}s · " +
                $"servergetrieben SH2/4→CH2/5={serverHeartbeatRequests:N0}, {heartbeatAge} · " +
                $"letzte Nicht-HB-Pakete: {recent}";
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Capture-derived NA2016 behavior: the Zone drives the heartbeat. The original
                // client capture shows an interval of about 30 s. Use a timeout ABOVE that period
                // so hundreds of idle load clients do not create synchronized 10-second
                // cancellation/restart churn on their NetworkStream reads.
                var packet = await connection.ReadPacketAsync(
                    TimeSpan.FromSeconds(45),
                    cancellationToken);

                if (packet.Header == 2 && packet.Type == 4)
                {
                    await connection.SendPacketAsync(
                        2,
                        5,
                        ReadOnlyMemory<byte>.Empty,
                        cancellationToken);
                    serverHeartbeatRequests++;
                    lastServerHeartbeatUtc = DateTimeOffset.UtcNow;
                    heartbeatProgress?.Invoke(
                        $"ZONE_HEARTBEAT_ROUNDTRIP · SH2/4 empfangen={serverHeartbeatRequests:N0} · CH2/5 gesendet={serverHeartbeatRequests:N0}");
                    continue;
                }

                // SH2/5 is not part of the captured Zone heartbeat direction. Drain it if a
                // nonstandard server ever sends one, but never use it as liveness evidence.
                if (packet.Header == 2 && packet.Type == 5)
                    continue;

                if (packet.Header == 4 && packet.Type == 2)
                {
                    throw new InvalidOperationException(
                        $"{role} meldete SH4/2 ConnectError während des Haltens · {HoldingTelemetry()}.");
                }

                RememberServerPacket(packet);
                // Drain non-heartbeat traffic so the TCP receive window remains healthy. The
                // packet history is retained so a later server-side disconnect can reveal which
                // periodic challenge/status request preceded it.
            }
            catch (TimeoutException)
            {
                // A 45-second idle interval is already longer than the captured ~30-second Zone
                // heartbeat cadence. Keep waiting, but preserve the session as diagnostic
                // evidence rather than generating high-frequency cancellation churn.
            }
            catch (EndOfStreamException) when (allowRemoteClose)
            {
                // After successful Zone ClientReady the authoritative proof is the live Zone
                // socket plus the hash-bound ShinePlayer l_ListNum. Some server paths can retire
                // the World client socket after handoff; that alone must not invalidate a player
                // that remains present in ShinePlayer throughout the stability window.
                return;
            }
            catch (EndOfStreamException ex)
            {
                throw new EndOfStreamException(
                    $"{role}: Fiesta-Verbindung wurde vom Server geschlossen · {HoldingTelemetry()}.",
                    ex);
            }
            catch (InvalidDataException ex) when (
                allowRemoteClose
                && IsIgnorablePostReadyWorldFrameTermination(ex))
            {
                // The World socket is non-authoritative after a successful Zone handoff. Under
                // load this build can terminate/retire that old stream with an invalid zero-sized
                // Fiesta frame. Treat it exactly like the already tolerated World remote-close;
                // the Zone socket remains strict and can never take this path.
                return;
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException(
                    $"{role}: ungültiges Fiesta-Framing während Holding · {ex.Message} · {HoldingTelemetry()}",
                    ex);
            }
            catch (IOException) when (allowRemoteClose)
            {
                return;
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"{role}: Fiesta-Verbindung brach während Heartbeat/Holding ab · {HoldingTelemetry()}.",
                    ex);
            }
            catch (SocketException) when (allowRemoteClose)
            {
                return;
            }
            catch (SocketException ex)
            {
                throw new SocketException(ex.ErrorCode);
            }
        }
    }

    // Preserve the last server opcodes and the exact point of transport termination
    // during CH6/1 -> SH6/2. No payload bytes from initialization packets are logged.
    private static async Task<FiestaPacket> WaitForInitialZoneLoginAckAsync(
        FiestaWireConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var recent = new Queue<string>();
        var received = 0;

        string Snapshot() => $"Empfangene Zone-Pakete vor SH6/2={received}; letzte Opcodes: " +
            (recent.Count == 0 ? "<keine>" : string.Join(", ", recent));

        while (DateTime.UtcNow < deadline)
        {
            FiestaPacket packet;
            try
            {
                packet = await connection.ReadPacketAsync(deadline - DateTime.UtcNow, cancellationToken);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested
                && (ex is TimeoutException or EndOfStreamException or IOException or SocketException
                    || (ex is InvalidDataException data
                        && data.Message.Contains("Fiesta-Frame meldet Länge 0", StringComparison.OrdinalIgnoreCase))))
            {
                throw new IOException($"Zone-Verbindung während CH6/1→SH6/2 unterbrochen · {Snapshot()} · " +
                    $"{ex.GetType().Name}: {ex.Message}", ex);
            }

            received++;
            recent.Enqueue($"SH{packet.Header}/{packet.Type}({packet.Body.Length}B)");
            while (recent.Count > 8)
                recent.Dequeue();

            if (packet.Header == 2 && packet.Type == 4)
            {
                await connection.SendPacketAsync(2, 5, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            if (packet.Header == 3 && packet.Type == 9)
                throw new InvalidOperationException($"Zone meldete SH3/9 Error · {Snapshot()}.");

            if (packet.Header == 4 && packet.Type == 2)
            {
                var code = packet.Body.Length >= 2
                    ? BinaryPrimitives.ReadUInt16LittleEndian(packet.Body.AsSpan(0, 2)).ToString()
                    : "<fehlt>";
                throw new InvalidOperationException(
                    $"World/Zone meldete SH4/2 ConnectError. UInt16LE={code} · {Snapshot()}.");
            }

            if (packet.Header == 5 && packet.Type == 4)
                throw new InvalidOperationException(
                    DescribeWorldSh54(packet, "während Zone-Handoff CH6/1→SH6/2") + " " + Snapshot());

            if (packet.Header == 6 && packet.Type == 2)
                return packet;
        }

        throw new TimeoutException($"Zone SH6/2 nach {timeout.TotalSeconds:N0}s nicht empfangen · {Snapshot()}.");
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
            {
                var errorCode = packet.Body.Length >= 2
                    ? BinaryPrimitives.ReadUInt16LittleEndian(packet.Body.AsSpan(0, 2))
                    : ushort.MaxValue;
                var body = packet.Body.Length == 0
                    ? "<leer>"
                    : Convert.ToHexString(packet.Body);
                throw new FiestaWorldCharacterLoginRejectedException(
                    errorCode,
                    $"World meldete NC_CHAR_LOGINFAIL_ACK SH4/2 nach Charakterauswahl " +
                    $"(err={(errorCode == ushort.MaxValue ? "<fehlt>" : errorCode.ToString())}, Body={body}).");
            }
            if (packet.Header == 5 && packet.Type == 4)
                throw new InvalidOperationException(DescribeWorldSh54(
                    packet, "nach CH4/1 Charakterauswahl (warten auf SH4/3 ZoneRedirect)"));

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
                var phase = expectedHeader == 5 && expectedType == 6
                    ? "nach CH5/1 CharacterCreate (warten auf SH5/6)"
                    : $"beim Warten auf SH{expectedHeader}/{expectedType} (CH5/1 noch nicht gesendet)";
                throw new InvalidOperationException(DescribeWorldSh54(packet, phase));
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

    private sealed class FiestaWorldCharacterNotVisibleException : InvalidOperationException
    {
        public FiestaWorldCharacterNotVisibleException(string message)
            : base(message)
        {
        }
    }

    private sealed class FiestaWorldCharacterLoginRejectedException : InvalidOperationException
    {
        public FiestaWorldCharacterLoginRejectedException(ushort errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public ushort ErrorCode { get; }
    }

    private sealed class FiestaInitialWorldTransferRejectedException : InvalidOperationException
    {
        public FiestaInitialWorldTransferRejectedException(string message)
            : base(message)
        {
        }

        public FiestaInitialWorldTransferRejectedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed record FiestaOutboundPacket(int Header, int Type, byte[] Body);

    private sealed record FiestaCharacterListEntry(
        byte Slot,
        uint CharacterNumber,
        string CharacterName);

    private sealed record FiestaCharacterListSnapshot(
        ushort RandomId,
        byte CharacterCount,
        IReadOnlyList<FiestaCharacterListEntry> Characters);

    // Maintains exactly one World session through Zone authentication and Holding.
    // A rejected pre-SH6/2 Zone transfer is not retried with a stale World ticket.
    private sealed class FiestaWorldConnectionLease : IAsyncDisposable
    {
        private FiestaWireConnection? _connection;

        public FiestaWorldConnectionLease(FiestaWireConnection initial)
            => _connection = initial;

        public FiestaWireConnection Connection
            => _connection ?? throw new InvalidOperationException(
                "Keine aktuelle World-Session für Zone-Transfer vorhanden.");

        public async ValueTask CloseAsync()
        {
            var old = _connection;
            _connection = null;
            if (old is not null)
                await old.DisposeAsync();
        }

        public void Set(FiestaWireConnection replacement)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            if (_connection is not null)
                throw new InvalidOperationException("Alte World-Session muss vor dem Ersetzen geschlossen werden.");
            _connection = replacement;
        }

        public ValueTask DisposeAsync() => CloseAsync();
    }

    private sealed record FiestaWorldEntryResult(
        FiestaEndpointRedirect? Zone,
        ushort RandomId,
        byte CharacterCount,
        bool CharacterCreated,
        FiestaWireConnection? WorldConnection);

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
