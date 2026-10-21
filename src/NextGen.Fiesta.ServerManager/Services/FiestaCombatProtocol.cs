using System.Buffers.Binary;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// NA2016 command/response layouts. Commands do not choose a safe target or prove
/// damage. Handles must come from the current session, never from test fixtures.
/// </summary>
public static class FiestaCombatProtocol
{
    public static byte[] SelectTarget(ushort handle)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 9 << 10 | 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), handle);
        return payload;
    }

    public static byte[] StartBasicAttack() => new byte[] { 0x2b, 0x24 };
    public static byte[] StopBasicAttack() => new byte[] { 0x32, 0x24 };

    // PDB PROTO_NC_BAT_TARGETINFO_CMD, size 30. Confirmed by the original
    // client capture: order@0, targethandle@1 (not @0), HP@3, max HP@7.
    public static FiestaCombatTargetInfo? ReadTargetInfo(int header, int type, ReadOnlySpan<byte> body)
    {
        if (header != 9 || type != 2) return null;
        if (body.Length != 30) throw new InvalidDataException("NA2016 target info must contain 30 body bytes.");
        return new FiestaCombatTargetInfo(body[0],
            BinaryPrimitives.ReadUInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[3..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[7..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[11..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[15..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[19..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[23..]), body[27],
            BinaryPrimitives.ReadUInt16LittleEndian(body[28..]));
    }

    // PDB-only layout until a current live hit has been observed. The original
    // capture used below contains selection/attack commands but NO hit response.
    // PROTO_NC_BAT_BASH_HIT_CMD: size 13; damage is a 15-bit value above bit 0.
    public static FiestaCombatBasicHit? ReadBasicHit(int header, int type, ReadOnlySpan<byte> body)
    {
        if (header != 9 || type != 44) return null;
        if (body.Length != 13) throw new InvalidDataException("NA2016 basic hit must contain 13 body bytes.");
        var packedDamage = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        return new FiestaCombatBasicHit(BinaryPrimitives.ReadUInt16LittleEndian(body),
            (ushort)(packedDamage >> 1), (packedDamage & 1) != 0, body[4],
            BinaryPrimitives.ReadUInt32LittleEndian(body[5..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[9..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[11..]));
    }

    public static void RunSelfTest()
    {
        // Original client stream 239, 2026-10-07. Historical handle is a fixture only.
        if (!SelectTarget(9062).SequenceEqual(Convert.FromHexString("01246623"))
            || !StartBasicAttack().SequenceEqual(Convert.FromHexString("2B24"))
            || !StopBasicAttack().SequenceEqual(Convert.FromHexString("3224")))
            throw new InvalidDataException("Combat commands differ from the original-client capture.");
        var captured = Convert.FromHexString("006623E8030000E803000064000000640000000000000000000000010100");
        var target = ReadTargetInfo(9, 2, captured);
        if (target != new FiestaCombatTargetInfo(0, 9062, 1000, 1000, 100, 100, 0, 0, 1, 1))
            throw new InvalidDataException("Target info differs from the captured server response.");

        // Synthetic PDB fixture, NOT a captured or live damage confirmation.
        // Target 0x1234, critical 37 damage, flags 0x25, HP 0x12345678,
        // speed 0x0102, change order 0xfffe: distinct values catch overlap/endianness.
        var hit = ReadBasicHit(9, 44, Convert.FromHexString("34124B0025785634120201FEFF"));
        if (hit != new FiestaCombatBasicHit(0x1234, 37, true, 0x25, 0x12345678, 0x0102, 0xfffe))
            throw new InvalidDataException("Basic hit differs from the PDB field/bit layout.");
        var maxDamage = ReadBasicHit(9, 44, Convert.FromHexString("3412FEFF000000000000000000"));
        if (maxDamage is null || maxDamage.Damage != 32767 || maxDamage.Critical)
            throw new InvalidDataException("Damage bitfield boundary was decoded incorrectly.");
        if (ReadTargetInfo(8, 2, Array.Empty<byte>()) is not null
            || ReadTargetInfo(9, 3, Array.Empty<byte>()) is not null
            || ReadBasicHit(9, 45, Array.Empty<byte>()) is not null)
            throw new InvalidDataException("Unrelated packet was decoded as a combat response.");
        foreach (var length in new[] { 0, 1, 29, 31 })
        {
            try { ReadTargetInfo(9, 2, new byte[length]); }
            catch (InvalidDataException) { continue; }
            throw new InvalidDataException("Unexpected target info layout was accepted.");
        }
        foreach (var length in new[] { 0, 12, 14, 15 })
        {
            try { ReadBasicHit(9, 44, new byte[length]); }
            catch (InvalidDataException) { continue; }
            throw new InvalidDataException("Unexpected basic hit layout was accepted.");
        }
    }
}

public sealed record FiestaCombatTargetInfo(byte Order, ushort Handle, uint HP, uint MaxHP,
    uint SP, uint MaxSP, uint LP, uint MaxLP, byte Level, ushort HPChangeOrder);

/// <summary>Decoded server event; caller must correlate it to the current session and target.</summary>
public sealed record FiestaCombatBasicHit(ushort TargetHandle, ushort Damage, bool Critical,
    byte Flags, uint RemainingHP, ushort AttackSpeed, ushort HPChangeOrder);
