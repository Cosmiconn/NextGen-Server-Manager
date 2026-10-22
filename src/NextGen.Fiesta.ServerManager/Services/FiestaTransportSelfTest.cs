using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>Loopback-only integration checks; never connects to a game service.</summary>
public static class FiestaTransportSelfTest
{
    public static async Task RunAsync()
    {
        FiestaMovementProtocol.RunSelfTest();
        FiestaCombatProtocol.RunSelfTest();
        FiestaMobVisibilityProtocol.RunSelfTest();
        FiestaMobVisibilitySession.RunSelfTest();
        await RunMobVisibilityTestsAsync();
        await RunMovementPilotTestsAsync();
        await WithConnection(async (client, server, token) =>
        {
            var waiting = FiestaHeadlessLoadClient.WaitForInitialZoneLoginAckAsync(
                client, TimeSpan.FromSeconds(2), token);
            // Server heartbeat before ACK must still be serviced.
            await server.GetStream().WriteAsync(new byte[] { 2, 4, 8 }, token);
            var response = await ReadFrame(server.GetStream(), token);
            new FiestaXorCipher(0).TransformInPlace(response);
            if (!response.SequenceEqual(new byte[] { 5, 8 }))
                throw new InvalidDataException("Login identity parsing blocked the pre-ACK heartbeat.");
            var frame = new byte[245];
            frame[0] = 244;
            frame[1] = 2;
            frame[2] = 24; // SH6/2
            Convert.FromHexString("E02E").CopyTo(frame, 3);
            Convert.FromHexString("8E2D0000412A0000").CopyTo(frame, 237);
            await server.GetStream().WriteAsync(frame, token);
            var ack = await waiting;
            if (ack.Actor != new FiestaZoneActorIdentity(12000, 11662, 10817)
                || ack.ReceivedBeforeAck != 1)
                throw new InvalidDataException("Zone login did not retain the authenticated actor identity.");
        });
        await WithConnection(async (client, server, token) =>
        {
            var waiting = FiestaHeadlessLoadClient.WaitForInitialZoneLoginAckAsync(
                client, TimeSpan.FromSeconds(2), token);
            await server.GetStream().WriteAsync(new byte[] { 4, 2, 24, 0, 0 }, token);
            var rejected = false;
            try { await waiting; }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidDataException("Truncated SH6/2 authenticated a Zone actor.");
        });
        await WithConnection(async (world, worldServer, token) =>
        {
            await WithConnection(async (zone, zoneServer, _) =>
            {
                var hold = FiestaHeadlessLoadClient.HoldSessionsAsync(world, zone,
                    TimeSpan.FromMilliseconds(500), null, token);
                worldServer.Dispose();
                await Task.Delay(100, token);
                if (hold.IsCompleted) throw new InvalidDataException("Normal World closure ended a healthy Zone hold.");
                await zoneServer.GetStream().WriteAsync(new byte[] { 2, 4, 8 }, token);
                var response = await ReadFrame(zoneServer.GetStream(), token);
                new FiestaXorCipher(0).TransformInPlace(response);
                if (!response.SequenceEqual(new byte[] { 5, 8 }))
                    throw new InvalidDataException("Zone heartbeat stopped after normal World closure.");
                await hold.WaitAsync(TimeSpan.FromSeconds(2), token);
            });
        });

        await WithConnection(async (world, worldServer, token) =>
        {
            await WithConnection(async (zone, zoneServer, _) =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var hold = FiestaHeadlessLoadClient.HoldSessionsAsync(world, zone,
                    TimeSpan.FromSeconds(8), null, cancellation.Token);
                cancellation.Cancel();
                var observed = false;
                try { await hold.WaitAsync(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) { observed = true; }
                if (!observed) throw new InvalidDataException("Caller cancellation was reported as a successful hold.");
            });
        });

        await WithConnection(async (world, worldServer, token) =>
        {
            await WithConnection(async (zone, zoneServer, _) =>
            {
                var hold = FiestaHeadlessLoadClient.HoldSessionsAsync(world, zone,
                    TimeSpan.FromSeconds(8), null, token);
                zoneServer.Dispose();
                var failedPromptly = false;
                try { await hold.WaitAsync(TimeSpan.FromSeconds(2), token); }
                catch (EndOfStreamException) { failedPromptly = true; }
                if (!failedPromptly) throw new InvalidDataException("Zone disconnect was hidden behind the idle World pump.");
            });
        });
        await WithConnection(async (client, server, token) =>
        {
            const int count = 128;
            var reader = Task.Run(async () =>
            {
                var seen = new HashSet<int>();
                var cipher = new FiestaXorCipher(0);
                for (var i = 0; i < count; i++)
                {
                    var payload = await ReadFrame(server.GetStream(), token);
                    cipher.TransformInPlace(payload);
                    if (payload.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(payload) != (8 << 10 | 25))
                        throw new InvalidDataException("Concurrent writes corrupted the opcode/cipher stream.");
                    var id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(2));
                    if (id < 0 || id >= count || !seen.Add(id) || payload.Length != 260 + id
                        || payload.Skip(6).Any(b => b != (byte)id))
                        throw new InvalidDataException("Concurrent writes interleaved, duplicated or lost a frame.");
                }
            }, token);
            var writers = Enumerable.Range(0, count).Select(id => Task.Run(async () =>
            {
                var payload = Enumerable.Repeat((byte)id, 260 + id).ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(payload, 8 << 10 | 25);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), id);
                await client.SendDecryptedPayloadAsync(payload, token);
            }, token)).ToArray();
            await Task.WhenAll(writers.Append(reader));
        });

        await WithConnection(async (client, server, token) =>
        {
            var invalidRejected = false;
            try { await client.SendDecryptedPayloadAsync(new byte[65536], token); }
            catch (ArgumentException) { invalidRejected = true; }
            if (!invalidRejected) throw new InvalidDataException("Oversized outgoing frame was accepted.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelObserved = false;
            try { await client.SendDecryptedPayloadAsync(FiestaMovementProtocol.Stop(100, 200), cancelled.Token); }
            catch (OperationCanceledException) { cancelObserved = true; }
            if (!cancelObserved) throw new InvalidDataException("Cancelled outgoing frame was sent.");
            var expected = FiestaMovementProtocol.Stop(300, 400);
            await client.SendDecryptedPayloadAsync(expected, token);
            var payload = await ReadFrame(server.GetStream(), token);
            new FiestaXorCipher(0).TransformInPlace(payload);
            if (!payload.SequenceEqual(expected))
                throw new InvalidDataException("Rejected/cancelled send changed the cipher position.");
        });

        await WithConnection(async (client, server, token) =>
        {
            // No frame bytes consumed: a heartbeat wait may legitimately time out.
            var timedOut = false;
            try { await client.ReadPacketAsync(TimeSpan.FromMilliseconds(100), token); }
            catch (TimeoutException) { timedOut = true; }
            if (!timedOut) throw new InvalidDataException("Idle read did not time out.");
            await server.GetStream().WriteAsync(new byte[] { 2, 4, 8 }, token);
            var packet = await client.ReadPacketAsync(TimeSpan.FromSeconds(2), token);
            if (packet.Header != 2 || packet.Type != 4 || packet.Body.Length != 0)
                throw new InvalidDataException("Idle timeout damaged the next heartbeat frame.");
        });

        foreach (var fragment in new[] { new byte[] { 0, 4 }, new byte[] { 4, 4 } })
        {
            await WithConnection(async (client, server, token) =>
            {
                await server.GetStream().WriteAsync(fragment, token);
                var rejected = false;
                try { await client.ReadPacketAsync(TimeSpan.FromMilliseconds(150), token); }
                catch (IOException) { rejected = true; }
                if (!rejected) throw new InvalidDataException("Partial frame timeout was treated as a recoverable idle timeout.");
                var disconnected = await server.GetStream().ReadAsync(new byte[1], token);
                if (disconnected != 0) throw new InvalidDataException("Corrupt frame stream was not closed.");
            });
        }

        await WithConnection(async (client, server, token) =>
        {
            // A frame may span several TCP reads without being malformed.
            var read = client.ReadPacketAsync(TimeSpan.FromSeconds(2), token);
            await server.GetStream().WriteAsync(new byte[] { 0, 4 }, token);
            await Task.Delay(20, token);
            await server.GetStream().WriteAsync(new byte[] { 0, 4, 8, 12, 34 }, token);
            var packet = await read;
            if (packet.Header != 2 || packet.Type != 4 || !packet.Body.SequenceEqual(new byte[] { 12, 34 }))
                throw new InvalidDataException("Valid fragmented frame was not reassembled.");
        });
    }

    private static async Task RunMobVisibilityTestsAsync()
    {
        await WithConnection(async (world, worldServer, token) =>
        {
            await WithConnection(async (zone, zoneServer, _) =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var actor = new FiestaZoneActorIdentity(12000, 11662, 10817);
                using var visibility = new FiestaMobVisibilitySession(actor);
                var hold = FiestaHeadlessLoadClient.HoldSessionsAsync(world, zone,
                    TimeSpan.FromSeconds(8), null, cancellation.Token, zoneActor: actor, mobVisibility: visibility);
                var zoneCipher = new FiestaXorCipher(0);
                var worldCipher = new FiestaXorCipher(0);
                async Task Barrier(TcpClient server, FiestaXorCipher cipher)
                {
                    await server.GetStream().WriteAsync(new byte[] { 2, 4, 8 }, token);
                    var reply = await ReadFrame(server.GetStream(), token);
                    cipher.TransformInPlace(reply);
                    if (!reply.SequenceEqual(new byte[] { 5, 8 }))
                        throw new InvalidDataException("Passive visibility interfered with heartbeat order.");
                }

                var appearance = new byte[152];
                appearance[0] = 151;
                appearance[1] = 8;
                appearance[2] = 28; // SH7/8
                Convert.FromHexString("6623021240143000000A28000028").CopyTo(appearance, 3);
                await worldServer.GetStream().WriteAsync(appearance, token);
                await Barrier(worldServer, worldCipher);
                if (visibility.Bind(9062) is not null)
                    throw new InvalidDataException("World packet contaminated Zone mob visibility.");
                await zoneServer.GetStream().WriteAsync(appearance, token);
                await Barrier(zoneServer, zoneCipher);
                var binding = visibility.Bind(9062)
                    ?? throw new InvalidDataException("Zone receive pump did not observe the current mob.");
                if (visibility.Snapshot().AppearanceCandidates.Count != 1)
                    throw new InvalidDataException("Passive observation lost the current candidate.");
                await zoneServer.GetStream().WriteAsync(new byte[] { 4, 14, 28, 0x66, 0x23 }, token);
                await Barrier(zoneServer, zoneCipher);
                if (visibility.IsCurrent(binding) || visibility.Snapshot().AppearanceCandidates.Count != 0)
                    throw new InvalidDataException("Zone receive pump retained a removed target.");
                // A malformed list must fail the hold promptly even while World
                // remains connected, rather than returning a usable observation.
                await zoneServer.GetStream().WriteAsync(new byte[] { 3, 9, 28, 1 }, token);
                var rejected = false;
                try { await hold.WaitAsync(TimeSpan.FromSeconds(2), token); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || visibility.IsCurrent(binding))
                    throw new InvalidDataException("Malformed Zone visibility did not invalidate the hold.");
            });
        });
    }

    private static async Task RunMovementPilotTestsAsync()
    {
        var actor = new FiestaZoneActorIdentity(12000, 100, 200);
        var plan = new FiestaMovementPilotPlan(100, 200, 110, 200)
        {
            StartDelay = TimeSpan.FromMilliseconds(20),
            TravelDuration = TimeSpan.FromMilliseconds(250),
            SettleDuration = TimeSpan.FromMilliseconds(40)
        };
        foreach (var invalid in new[]
        {
            plan with { EndX = 100 }, plan with { EndX = 111 },
            plan with { EndX = uint.MaxValue }, plan with { TravelDuration = TimeSpan.Zero },
            plan with { StartDelay = TimeSpan.FromSeconds(-1) }
        })
        {
            var rejected = false;
            try { invalid.Validate(TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new InvalidDataException("Invalid movement pilot plan was accepted.");
        }
        var shortHoldRejected = false;
        try { plan.Validate(TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException) { shortHoldRejected = true; }
        if (!shortHoldRejected) throw new InvalidDataException("Movement pilot outlives its session hold.");
        var rampRejected = false;
        try
        {
            new FiestaLoadRampOptions
            {
                ClientOptions = new FiestaHeadlessProbeOptions { MovementPilot = plan }
            }.Validate();
        }
        catch (ArgumentException ex) when (ex.Message.Contains("individual client invocation")) { rampRejected = true; }
        if (!rampRejected) throw new InvalidDataException("Ramp silently discarded the actor-specific movement plan.");

        await WithConnection(async (world, worldServer, token) =>
        {
            await WithConnection(async (zone, zoneServer, _) =>
            {
                var hold = FiestaHeadlessLoadClient.HoldSessionsAsync(world, zone,
                    TimeSpan.FromMilliseconds(800), null, token, plan, actor);
                var cipher = new FiestaXorCipher(0);
                var run = await ReadFrame(zoneServer.GetStream(), token);
                cipher.TransformInPlace(run);
                if (!run.SequenceEqual(FiestaMovementProtocol.Run(100, 200, 110, 200)))
                    throw new InvalidDataException("Movement pilot did not send the planned run first.");
                await zoneServer.GetStream().WriteAsync(new byte[] { 2, 4, 8 }, token);
                var remaining = new List<byte[]>();
                for (var i = 0; i < 2; i++)
                {
                    var payload = await ReadFrame(zoneServer.GetStream(), token);
                    cipher.TransformInPlace(payload);
                    remaining.Add(payload);
                }
                if (remaining.Count(p => p.SequenceEqual(new byte[] { 5, 8 })) != 1
                    || remaining.Count(p => p.SequenceEqual(FiestaMovementProtocol.Stop(110, 200))) != 1)
                    throw new InvalidDataException("Movement/heartbeat frame order or XOR state was corrupted.");
                var evidence = await hold;
                if (evidence is null || evidence.ActorHandle != actor.Handle
                    || evidence.StartX != 100 || evidence.EndX != 110
                    || evidence.StopSentUtc < evidence.RunSentUtc || evidence.ServerPositionVerified)
                    throw new InvalidDataException("Movement send evidence was missing or claimed server acceptance.");
            });
        });

        await WithConnection(async (world, worldServer, token) =>
        {
            await WithConnection(async (zone, zoneServer, _) =>
            {
                var rejected = false;
                try
                {
                    await FiestaHeadlessLoadClient.HoldSessionsAsync(world, zone,
                        TimeSpan.FromSeconds(1), null, token, plan, actor with { LoginX = 99 });
                }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || zoneServer.Available != 0)
                    throw new InvalidDataException("Mismatched current login position allowed movement traffic.");
            });
        });

        await WithConnection(async (world, worldServer, token) =>
        {
            await WithConnection(async (zone, zoneServer, _) =>
            {
                var hold = FiestaHeadlessLoadClient.HoldSessionsAsync(world, zone,
                    TimeSpan.FromSeconds(8), null, token,
                    plan with { TravelDuration = TimeSpan.FromSeconds(3) }, actor);
                await ReadFrame(zoneServer.GetStream(), token);
                await zoneServer.GetStream().WriteAsync(new byte[] { 10, 27, 32, 100, 0, 0, 0, 200, 0, 0, 0 }, token);
                var rejected = false;
                try { await hold.WaitAsync(TimeSpan.FromSeconds(2), token); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Zone rejected movement")) { rejected = true; }
                if (!rejected) throw new InvalidDataException("Server movement rejection did not end the pilot promptly.");
                // Hold has awaited the cancelled sender. The caller owns and
                // disposes the Zone lease after this failed hold returns.
                if (zoneServer.Available != 0)
                    throw new InvalidDataException("Pilot continued sending after a server rejection.");
            });
        });

        await WithConnection(async (world, worldServer, token) =>
        {
            await WithConnection(async (zone, zoneServer, _) =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var hold = FiestaHeadlessLoadClient.HoldSessionsAsync(world, zone,
                    TimeSpan.FromSeconds(8), null, cancellation.Token,
                    plan with { TravelDuration = TimeSpan.FromSeconds(3) }, actor);
                await ReadFrame(zoneServer.GetStream(), token);
                cancellation.Cancel();
                var cancelled = false;
                try { await hold.WaitAsync(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) { cancelled = true; }
                if (!cancelled) throw new InvalidDataException("Cancelled pilot produced a successful result.");
                if (await zoneServer.GetStream().ReadAsync(new byte[1], token) != 0)
                    throw new InvalidDataException("Cancelled pilot sent a later stop command.");
            });
        });
    }

    private static async Task WithConnection(Func<FiestaWireConnection, TcpClient, CancellationToken, Task> test)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(timeout.Token);
        var connect = FiestaWireConnection.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), timeout.Token);
        using var server = await accept;
        server.NoDelay = true;
        await server.GetStream().WriteAsync(new byte[] { 4, 7, 8, 0, 0 }, timeout.Token);
        await using var client = await connect;
        await test(client, server, timeout.Token);
    }

    private static async Task<byte[]> ReadFrame(NetworkStream stream, CancellationToken token)
    {
        var first = new byte[1];
        await stream.ReadExactlyAsync(first, token);
        int size = first[0];
        if (size == 0)
        {
            var length = new byte[2];
            await stream.ReadExactlyAsync(length, token);
            size = BinaryPrimitives.ReadUInt16LittleEndian(length);
        }
        if (size < 2) throw new InvalidDataException("Invalid loopback frame length.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token);
        return payload;
    }
}
