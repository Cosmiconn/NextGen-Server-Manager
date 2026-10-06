using System.Runtime.InteropServices;

namespace NextGen.Fiesta.ServerManager.Services;

public sealed record SystemResourceSnapshot(double CpuPercent, double TotalMemoryMb, double AvailableMemoryMb)
{
    public double MemoryUsedPercent => TotalMemoryMb > 0 ? (1.0 - AvailableMemoryMb / TotalMemoryMb) * 100.0 : 0;
    public string Summary => TotalMemoryMb > 0
        ? $"PC CPU {CpuPercent:F0}% · RAM {MemoryUsedPercent:F0}% ({AvailableMemoryMb:F0} MB frei)"
        : $"PC CPU {CpuPercent:F0}% · RAM unbekannt";
}

public sealed class SystemResourceMonitorService
{
    private readonly object _gate = new();
    private ulong? _prevIdle;
    private ulong? _prevKernel;
    private ulong? _prevUser;

    public SystemResourceSnapshot Sample()
    {
        var cpu = SampleCpu();
        var (total, available) = SampleMemory();
        return new SystemResourceSnapshot(cpu, total, available);
    }

    private double SampleCpu()
    {
        try
        {
            if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return 0;
            var idle = ToUInt64(idleFt);
            var kernel = ToUInt64(kernelFt);
            var user = ToUInt64(userFt);
            lock (_gate)
            {
                if (!_prevIdle.HasValue)
                {
                    _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
                    return 0;
                }
                var idleDelta = idle - _prevIdle.Value;
                var kernelDelta = kernel - _prevKernel!.Value;
                var userDelta = user - _prevUser!.Value;
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
                var total = kernelDelta + userDelta;
                if (total == 0) return 0;
                return Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
            }
        }
        catch { return 0; }
    }

    private static (double TotalMb, double AvailableMb) SampleMemory()
    {
        try
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref ms)) return (0, 0);
            return (ms.ullTotalPhys / 1024d / 1024d, ms.ullAvailPhys / 1024d / 1024d);
        }
        catch { return (0, 0); }
    }

    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
