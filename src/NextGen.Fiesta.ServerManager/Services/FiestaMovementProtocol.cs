using System.Buffers.Binary;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Capture/PDB-verified NA2016 movement wire formats. Encoding a command does not
/// establish a safe path or prove that the server accepted it. No network effects.
/// </summary>
public static class FiestaMovementProtocol
{
    public static byte[] Run(uint fromX, uint fromY, uint toX, uint toY)
    {
        var payload = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 8 << 10 | 25);
        WritePoint(payload.AsSpan(2), fromX, fromY);
        WritePoint(payload.AsSpan(10), toX, toY);
        return payload;
    }

    public static byte[] Stop(uint x, uint y)
    {
        var payload = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 8 << 10 | 18);
        WritePoint(payload.AsSpan(2), x, y);
        return payload;
    }

    // Only decode server movement broadcasts. Do not count these as acknowledgments
    // for a particular test actor without its current session/handle correlation.
    public static FiestaObservedMovement? ReadBroadcast(int header, int type, ReadOnlySpan<byte> body)
    {
        if (header != 8 || type is not (24 or 26)) return null;
        if (body.Length != 22) throw new InvalidDataException("NA2016 movement broadcast must contain 22 body bytes.");
        return new FiestaObservedMovement(BinaryPrimitives.ReadUInt16LittleEndian(body),
            BinaryPrimitives.ReadUInt32LittleEndian(body[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[14..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[18..]), type == 26);
    }

    private static void WritePoint(Span<byte> destination, uint x, uint y)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, x);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], y);
    }

    public static void RunSelfTest()
    {
        // Anonymized original-client capture: 2026-10-07, Zone stream 239.
        if (!Run(11662, 10817, 11710, 10904).SequenceEqual(Convert.FromHexString("19208E2D0000412A0000BE2D0000982A0000"))
            || !Stop(11707, 10896).SequenceEqual(Convert.FromHexString("1220BB2D0000902A0000")))
            throw new InvalidDataException("Movement encoding differs from the original-client capture.");
        var broadcast = ReadBroadcast(8, 24, Convert.FromHexString("6623143000000A28000037300000102800001D006E20"));
        if (broadcast != new FiestaObservedMovement(9062, 12308, 10250, 12343, 10256, 29, false)
            || ReadBroadcast(9, 24, Array.Empty<byte>()) is not null)
            throw new InvalidDataException("Movement broadcast decoding differs from the captured PDB layout.");
        try { ReadBroadcast(8, 26, new byte[21]); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Truncated movement broadcast was accepted.");
    }
}

public sealed record FiestaObservedMovement(ushort Handle, uint FromX, uint FromY,
    uint ToX, uint ToY, ushort Speed, bool Running);
