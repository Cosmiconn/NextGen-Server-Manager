using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NextGen.Fiesta.ServerManager.Models;

namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Resolves the effective WorldManager session limits from the exact NA2016 runtime
/// whenever possible. Runtime values win over ServerInfo so a changed config is never
/// presented as already-active capacity before the WorldManager has actually rebuilt
/// its session pool.
/// </summary>
public sealed class WorldManagerCapacityProbe
{
    private const int StockClientLimit = 1500;
    private const int StockZoneLimit = 100;
    private const long WmUserLimitRva = 0x00156BAC;
    private const long WmMaxSessionsRva = 0x00156BB8;
    private const long WmNumSessionsRva = 0x00156BBC;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;

    private readonly ServerInfoParser _parser = new();

    public WorldManagerLimitProbeResult Read(string serverRoot, FiestaServiceEntry wm)
    {
        var configuredClient = StockClientLimit;
        var configuredZone = StockZoneLimit;
        try
        {
            var serverInfo = Path.Combine(serverRoot ?? string.Empty, "9Data", "ServerInfo", "ServerInfo.txt");
            var entries = _parser.Parse(serverInfo);
            configuredClient = entries.FirstOrDefault(IsWorldManagerClientEntry)?.MaxAccept ?? StockClientLimit;
            configuredZone = entries.FirstOrDefault(IsWorldManagerZoneEntry)?.MaxAccept ?? StockZoneLimit;
        }
        catch
        {
            configuredClient = StockClientLimit;
            configuredZone = StockZoneLimit;
        }

        if (wm.State != ServiceRuntimeState.Running || !wm.ProcessId.HasValue || wm.ProcessId.Value <= 0)
        {
            return new WorldManagerLimitProbeResult
            {
                ConfiguredClientLimit = configuredClient,
                ConfiguredZoneSessionLimit = configuredZone,
                ActiveClientLimit = configuredClient,
                Source = "ServerInfo (WorldManager nicht laufend)",
                Detail = $"Nächster Start: Client-Pool {configuredClient:N0}, Zone/S2S {configuredZone:N0}."
            };
        }

        if (!File.Exists(wm.ExecutablePath) || !HashEquals(wm.ExecutablePath, AdaptiveHookService.BaselineWorldManagerSha256))
        {
            return new WorldManagerLimitProbeResult
            {
                ConfiguredClientLimit = configuredClient,
                ConfiguredZoneSessionLimit = configuredZone,
                ActiveClientLimit = configuredClient,
                Source = "ServerInfo (Runtime nicht hash-verifiziert)",
                Detail = "Der laufende WorldManager entspricht nicht dem verifizierten NA2016-Baseline-Hash; Runtime-Adressen werden nicht gelesen."
            };
        }

        try
        {
            using var process = Process.GetProcessById(wm.ProcessId.Value);
            var baseAddress = process.MainModule?.BaseAddress ?? IntPtr.Zero;
            if (baseAddress == IntPtr.Zero)
                return ConfigFallback(configuredClient, configuredZone, "WM-Modulbasis nicht lesbar");

            var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, wm.ProcessId.Value);
            if (handle == IntPtr.Zero)
                return ConfigFallback(configuredClient, configuredZone, "OpenProcess für Runtime-Probe fehlgeschlagen");

            try
            {
                if (!TryReadInt32(handle, baseAddress, WmUserLimitRva, out var userLimit)
                    || !TryReadInt32(handle, baseAddress, WmMaxSessionsRva, out var maxSessions)
                    || !TryReadInt32(handle, baseAddress, WmNumSessionsRva, out var numSessions))
                    return ConfigFallback(configuredClient, configuredZone, "WM-Runtimezähler konnten nicht gelesen werden");

                if (maxSessions < 1 || maxSessions > 100000 || numSessions < 0 || numSessions > maxSessions || userLimit < 0 || userLimit > 100000)
                    return ConfigFallback(configuredClient, configuredZone, "WM-Runtimewerte sind unplausibel");

                var admissionLimit = userLimit > 0 ? Math.Min(userLimit, maxSessions) : maxSessions;
                var restartPending = maxSessions != configuredClient;
                var detail = restartPending
                    ? $"Aktiv: m_MaxSessions={maxSessions:N0}, g_UserLimit={userLimit:N0}; ServerInfo={configuredClient:N0}. WorldManager-Neustart erforderlich, damit der konfigurierte Hard-Pool aktiv wird."
                    : $"Aktiv verifiziert: m_NumSessions={numSessions:N0}, m_MaxSessions={maxSessions:N0}, g_UserLimit={userLimit:N0}.";

                return new WorldManagerLimitProbeResult
                {
                    RuntimeVerified = true,
                    RuntimeNumSessions = numSessions,
                    RuntimeMaxSessions = maxSessions,
                    RuntimeUserLimit = userLimit,
                    ConfiguredClientLimit = configuredClient,
                    ConfiguredZoneSessionLimit = configuredZone,
                    ActiveClientLimit = Math.Max(1, admissionLimit),
                    RestartPending = restartPending,
                    Source = "WM Runtime (hash-verifiziert)",
                    Detail = detail
                };
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch (Exception ex)
        {
            return ConfigFallback(configuredClient, configuredZone, ex.Message);
        }
    }

    private static WorldManagerLimitProbeResult ConfigFallback(int configuredClient, int configuredZone, string reason) => new()
    {
        ConfiguredClientLimit = configuredClient,
        ConfiguredZoneSessionLimit = configuredZone,
        ActiveClientLimit = configuredClient,
        Source = "ServerInfo (Runtime nicht verifiziert)",
        Detail = reason
    };

    private static bool IsWorldManagerClientEntry(ServerInfoEntry e) =>
        e.Name.Contains("_WM", StringComparison.OrdinalIgnoreCase) && e.ConnectionKind == 20;

    private static bool IsWorldManagerZoneEntry(ServerInfoEntry e) =>
        e.Name.Contains("_WM", StringComparison.OrdinalIgnoreCase) && e.ConnectionKind == 6;

    private static bool HashEquals(string path, string expected)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadInt32(IntPtr handle, IntPtr baseAddress, long rva, out int value)
    {
        value = 0;
        try
        {
            var address = IntPtr.Add(baseAddress, checked((int)rva));
            var bytes = new byte[4];
            if (!ReadProcessMemory(handle, address, bytes, bytes.Length, out var read) || read.ToInt64() != bytes.Length)
                return false;
            value = BitConverter.ToInt32(bytes, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
