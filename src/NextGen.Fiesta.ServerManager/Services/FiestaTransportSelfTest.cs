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
