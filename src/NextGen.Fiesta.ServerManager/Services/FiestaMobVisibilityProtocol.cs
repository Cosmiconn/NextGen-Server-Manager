using System.Buffers.Binary;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Mob appearance/removal layouts confirmed against the NA2016 PDB and original
/// client capture. An appearance does not establish that a mob is attackable,
/// in range, alive, or still visible when a later command is sent.
/// </summary>
public static class FiestaMobVisibilityProtocol
{
    private const int MobBodyLength = 149;

    public static IReadOnlyList<FiestaVisibleMob>? ReadAppearances(
        int header, int type, ReadOnlySpan<byte> body)
    {
        if (header != 7 || type is not (8 or 9)) return null;
        if (type == 8)
        {
            if (body.Length != MobBodyLength)
                throw new InvalidDataException("NA2016 mob appearance must contain 149 body bytes.");
            return new[] { ReadMob(body) };
        }

        // PROTO_NC_BRIEFINFO_MOB_CMD: byte count, then packed 149-byte entries.
        // Validate the entire frame before exposing any partial visibility state.
        if (body.Length == 0 || body.Length != 1 + body[0] * MobBodyLength)
            throw new InvalidDataException("NA2016 mob list count does not match its body length.");
        var mobs = new FiestaVisibleMob[body[0]];
        var handles = new HashSet<ushort>();
        for (var i = 0; i < mobs.Length; i++)
        {
            mobs[i] = ReadMob(body.Slice(1 + i * MobBodyLength, MobBodyLength));
            if (!handles.Add(mobs[i].Handle))
                throw new InvalidDataException("NA2016 mob list contains an ambiguous duplicate handle.");
        }
        return mobs;
    }

    public static ushort? ReadRemovedHandle(int header, int type, ReadOnlySpan<byte> body)
    {
        if (header != 7 || type != 14) return null;
        if (body.Length != 2)
            throw new InvalidDataException("NA2016 brief-info removal must contain two body bytes.");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }

    // PROTO_NC_BRIEFINFO_REGENMOB_CMD: handle@0, mode@2, mobid@3,
    // SHINE_COORD_TYPE@5 (X@5, Y@9, direction@13). Remaining bytes are opaque.
    private static FiestaVisibleMob ReadMob(ReadOnlySpan<byte> body) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(body), body[2],
        BinaryPrimitives.ReadUInt16LittleEndian(body[3..]),
        BinaryPrimitives.ReadUInt32LittleEndian(body[5..]),
        BinaryPrimitives.ReadUInt32LittleEndian(body[9..]), body[13]);

    public static void RunSelfTest()
    {
        // Original stream 239: this mob appeared in SH7/9 at server packet 57,
        // was selected in SH9/2 at 113, and disappeared in SH7/14 at 203.
        // Historical handle/template/coordinates are fixtures only.
        var captured = Convert.FromHexString(
            "6623021240143000000A28000028000000000000000000000000000000000000000000000000000000000000000000000000000800000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000200");
        var expected = new FiestaVisibleMob(9062, 2, 16402, 12308, 10250, 40);
        var list = new byte[1 + captured.Length];
        list[0] = 1;
        captured.CopyTo(list, 1);
        if (ReadAppearances(7, 9, list) is not { Count: 1 } decoded || decoded[0] != expected)
            throw new InvalidDataException("Mob list identity/coordinates differ from the captured fixture.");
        // Same struct is used by individual regeneration packets, confirmed by
        // all 28 SH7/8 frames in the original capture (each 149 bytes).
        if (ReadAppearances(7, 8, captured) is not { Count: 1 } single || single[0] != expected
            || ReadRemovedHandle(7, 14, Convert.FromHexString("6623")) != expected.Handle)
            throw new InvalidDataException("Mob appearance/removal layout mismatch.");

        var pair = new byte[1 + 2 * MobBodyLength];
        pair[0] = 2;
        captured.CopyTo(pair, 1);
        captured.CopyTo(pair, 1 + MobBodyLength);
        // Synthetic second entry distinguishes stride, handle and coordinate offsets.
        BinaryPrimitives.WriteUInt16LittleEndian(pair.AsSpan(1 + MobBodyLength), 0x1234);
        BinaryPrimitives.WriteUInt32LittleEndian(pair.AsSpan(1 + MobBodyLength + 5), 0x12345678);
        var two = ReadAppearances(7, 9, pair)!;
        if (two.Count != 2 || two[0] != expected || two[1].Handle != 0x1234 || two[1].X != 0x12345678)
            throw new InvalidDataException("Packed mob list stride was decoded incorrectly.");
        if (ReadAppearances(7, 9, new byte[] { 0 })?.Count != 0
            || ReadAppearances(6, 9, Array.Empty<byte>()) is not null
            || ReadAppearances(7, 7, Array.Empty<byte>()) is not null
            || ReadRemovedHandle(7, 8, Array.Empty<byte>()) is not null)
            throw new InvalidDataException("Empty or unrelated visibility packet was decoded incorrectly.");
        foreach (var length in new[] { 0, 148, 150 })
            MustReject(() => ReadAppearances(7, 8, new byte[length]));
        foreach (var malformed in new[] { Array.Empty<byte>(), new byte[] { 1 }, list[..^1], list.Concat(new byte[] { 0 }).ToArray() })
            MustReject(() => ReadAppearances(7, 9, malformed));
        pair[0] = 1;
        MustReject(() => ReadAppearances(7, 9, pair));
        pair[0] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(pair.AsSpan(1 + MobBodyLength), expected.Handle);
        MustReject(() => ReadAppearances(7, 9, pair));
        foreach (var length in new[] { 0, 1, 3 })
            MustReject(() => ReadRemovedHandle(7, 14, new byte[length]));
    }

    private static void MustReject(Action decode)
    {
        try { decode(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Malformed visibility packet was accepted.");
    }
}

public sealed record FiestaVisibleMob(ushort Handle, byte Mode, ushort MobId, uint X, uint Y, byte Direction);
