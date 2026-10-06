using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Hash-bound, read-only probe for ShinePlayer/ShineMob/ShineNPC pool occupancy.
/// Exactly two NA2016 Zone images are accepted: the verified stock build and the
/// independently certified 2000/12000/512 offline-patch image. No process memory is written.
/// </summary>
public sealed class ZoneObjectPoolProbe
{
    private const int StockPlayerLimit = 1500;
    private const int StockMobLimit = 8000;
    private const int StockNpcLimit = 256;

    private const int CertifiedPlayerLimit = ZonePoolOfflineWriterSelfTest.PlayerTarget;
    private const int CertifiedMobLimit = ZonePoolOfflineWriterSelfTest.MobTarget;
    private const int CertifiedNpcLimit = ZonePoolOfflineWriterSelfTest.NpcTarget;

    // PDB/public symbol ?shineobjmanager@@3VShineObjectManager@@A
    // preferred VA 0x132826B8, image base 0x00400000.
    private const long ShineObjectManagerRva = 0x12E826B8;

    // ShineObjectManager field layout from Zone.pdb:
    // som_Player @ +0xAC, som_NPC @ +0xCC, som_Mob @ +0x10C.
    // Each ShineObjectEachList derives from List<ShineObject>; l_MaxSize is +0x04
    // and l_ListNum (live occupied slots) is +0x14.
    private const int PlayerMaxOffset = 0xB0;
    private const int PlayerCountOffset = 0xC0;
    private const int NpcMaxOffset = 0xD0;
    private const int NpcCountOffset = 0xE0;
    private const int MobMaxOffset = 0x110;
    private const int MobCountOffset = 0x120;

    // Pool array pointers, used as an extra initialization sanity check.
    private const int PlayerArrayOffset = 0x24;
    private const int NpcArrayOffset = 0x28;
    private const int MobArrayOffset = 0x30;

    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;

    public ZoneObjectPoolRuntimeSnapshot Read(FiestaServiceEntry zone)
    {
        if (zone.State != ServiceRuntimeState.Running || !zone.ProcessId.HasValue || zone.ProcessId.Value <= 0)
            return Unverified("Zone läuft nicht / PID nicht verfügbar");

        if (!File.Exists(zone.ExecutablePath))
            return Unverified("Zone.exe wurde am erwarteten Pfad nicht gefunden");

        var binaryHash = TrySha256(zone.ExecutablePath);
        var profile = ResolveProfile(binaryHash);
        if (profile is null)
            return Unverified(
                "Zone.exe entspricht weder dem verifizierten NA2016-Stock-Hash noch dem zertifizierten 2000/12000/512-Patch-Hash",
                binaryHash: binaryHash);

        try
        {
            using var process = Process.GetProcessById(zone.ProcessId.Value);
            var mainModule = process.MainModule;
            var moduleBase = mainModule?.BaseAddress ?? IntPtr.Zero;
            if (moduleBase == IntPtr.Zero)
                return Unverified("Zone-Modulbasis nicht lesbar", profile, binaryHash);

            var runningImagePath = mainModule?.FileName;
            if (string.IsNullOrWhiteSpace(runningImagePath) || !PathEquals(runningImagePath, zone.ExecutablePath))
                return Unverified(
                    $"Laufender Zone-Prozess stammt nicht aus dem erwarteten Pfad: {runningImagePath ?? "<unbekannt>"}",
                    profile,
                    binaryHash);

            var manager = IntPtr.Add(moduleBase, checked((int)ShineObjectManagerRva));
            var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, zone.ProcessId.Value);
            if (handle == IntPtr.Zero)
                return Unverified("OpenProcess für Zone-Pool-Probe fehlgeschlagen", profile, binaryHash);

            try
            {
                if (!TryReadUInt32(handle, manager, PlayerArrayOffset, out var playerArray)
                    || !TryReadUInt32(handle, manager, NpcArrayOffset, out var npcArray)
                    || !TryReadUInt32(handle, manager, MobArrayOffset, out var mobArray))
                    return Unverified("Zone-Pool-Arrayzeiger konnten nicht gelesen werden", profile, binaryHash);

                if (playerArray == 0 || npcArray == 0 || mobArray == 0)
                    return Unverified("ShineObjectManager ist noch nicht vollständig initialisiert", profile, binaryHash);

                if (!TryReadUInt16(handle, manager, PlayerMaxOffset, out var playerMax)
                    || !TryReadUInt16(handle, manager, PlayerCountOffset, out var playerCount)
                    || !TryReadUInt16(handle, manager, NpcMaxOffset, out var npcMax)
                    || !TryReadUInt16(handle, manager, NpcCountOffset, out var npcCount)
                    || !TryReadUInt16(handle, manager, MobMaxOffset, out var mobMax)
                    || !TryReadUInt16(handle, manager, MobCountOffset, out var mobCount))
                    return Unverified("Zone-Poolzähler konnten nicht gelesen werden", profile, binaryHash);

                // The maxima must agree with the exact hash-bound profile. This is the runtime
                // proof that the expected ShineObjectEachList cardinalities were initialized.
                if (playerMax != profile.PlayerLimit || mobMax != profile.MobLimit || npcMax != profile.NpcLimit)
                {
                    return Unverified(
                        $"Hashprofil {profile.Name}, aber Runtime-Maxima unerwartet: " +
                        $"Player {playerMax}/{profile.PlayerLimit}, Mob {mobMax}/{profile.MobLimit}, NPC {npcMax}/{profile.NpcLimit}",
                        profile,
                        binaryHash);
                }

                if (playerCount > playerMax || mobCount > mobMax || npcCount > npcMax)
                    return Unverified("Poolbelegung überschreitet l_MaxSize; Layoutprüfung fehlgeschlagen", profile, binaryHash);

                return new ZoneObjectPoolRuntimeSnapshot
                {
                    RuntimeVerified = true,
                    PlayerCount = playerCount,
                    PlayerLimit = playerMax,
                    MobCount = mobCount,
                    MobLimit = mobMax,
                    NpcCount = npcCount,
                    NpcLimit = npcMax,
                    BinaryProfile = profile.Name,
                    BinarySha256 = binaryHash,
                    Detail = $"Hash-verifizierte ShineObjectManager l_ListNum/l_MaxSize Runtimezähler; Profil {profile.Name}."
                };
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch (Exception ex)
        {
            return Unverified(ex.Message, profile, binaryHash);
        }
    }

    private static ZoneBinaryProfile? ResolveProfile(string hash)
    {
        if (hash.Equals(AdaptiveHookService.BaselineZoneSha256, StringComparison.OrdinalIgnoreCase))
            return new ZoneBinaryProfile("NA2016_STOCK", StockPlayerLimit, StockMobLimit, StockNpcLimit);

        if (hash.Equals(ZonePoolOfflineWriterSelfTest.ExpectedPatchedSha256, StringComparison.OrdinalIgnoreCase))
            return new ZoneBinaryProfile(
                "NEXTGEN_CERTIFIED_2000_12000_512",
                CertifiedPlayerLimit,
                CertifiedMobLimit,
                CertifiedNpcLimit);

        return null;
    }

    private static ZoneObjectPoolRuntimeSnapshot Unverified(
        string detail,
        ZoneBinaryProfile? profile = null,
        string binaryHash = "")
        => new()
        {
            RuntimeVerified = false,
            PlayerLimit = profile?.PlayerLimit ?? StockPlayerLimit,
            MobLimit = profile?.MobLimit ?? StockMobLimit,
            NpcLimit = profile?.NpcLimit ?? StockNpcLimit,
            BinaryProfile = profile?.Name ?? "UNVERIFIED",
            BinarySha256 = binaryHash,
            Detail = detail
        };

    private static string TrySha256(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadUInt16(IntPtr handle, IntPtr baseAddress, int offset, out ushort value)
    {
        value = 0;
        var bytes = new byte[2];
        var address = IntPtr.Add(baseAddress, offset);
        if (!ReadProcessMemory(handle, address, bytes, bytes.Length, out var read) || read.ToInt64() != bytes.Length)
            return false;
        value = BitConverter.ToUInt16(bytes, 0);
        return true;
    }

    private static bool TryReadUInt32(IntPtr handle, IntPtr baseAddress, int offset, out uint value)
    {
        value = 0;
        var bytes = new byte[4];
        var address = IntPtr.Add(baseAddress, offset);
        if (!ReadProcessMemory(handle, address, bytes, bytes.Length, out var read) || read.ToInt64() != bytes.Length)
            return false;
        value = BitConverter.ToUInt32(bytes, 0);
        return true;
    }

    private sealed record ZoneBinaryProfile(string Name, int PlayerLimit, int MobLimit, int NpcLimit);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
