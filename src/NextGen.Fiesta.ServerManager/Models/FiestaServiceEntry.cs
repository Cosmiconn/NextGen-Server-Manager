using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NextGen.Fiesta.ServerManager.Models;

public sealed class FiestaServiceEntry : INotifyPropertyChanged
{
    private ServiceRuntimeState _state = ServiceRuntimeState.Unknown;
    private HealthState _health = HealthState.Unknown;
    private bool _portOpen;
    private string _statusText = "Unbekannt";
    private DateTime? _lastStateChange;
    private double _cpuPercent;
    private double _workingSetMb;
    private double _privateMemoryMb;
    private double _virtualMemoryMb;
    private double _cpuCorePercent;
    private int _establishedClientConnections;
    private int _establishedInternalConnections;
    private int _handleCount;
    private int _threadCount;
    private int? _portOwnerPid;
    private DateTime? _lastLogActivity;

    public required string DisplayName { get; init; }
    public required string ServiceName { get; init; }
    public required FiestaServiceKind Kind { get; init; }
    public required string DirectoryPath { get; init; }
    public required string ExecutablePath { get; init; }
    public int? ZoneNumber { get; init; }
    public int? ClientPort { get; set; }
    public int? InternalPort { get; set; }
    public int? OptoolPort { get; set; }
    public string? ConfigPath { get; set; }
    public string? PdbPath { get; set; }
    public int? ProcessId { get; set; }
    public DateTime? ProcessStartTime { get; set; }
    public string? LastError { get; set; }

    public ServiceRuntimeState State
    {
        get => _state;
        set { if (_state != value) { _state = value; LastStateChange = DateTime.Now; OnPropertyChanged(); } }
    }

    public HealthState Health
    {
        get => _health;
        set { if (_health != value) { _health = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthSymbol)); } }
    }

    public bool PortOpen
    {
        get => _portOpen;
        set { if (_portOpen != value) { _portOpen = value; OnPropertyChanged(); } }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public DateTime? LastStateChange
    {
        get => _lastStateChange;
        private set { _lastStateChange = value; OnPropertyChanged(); }
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        set { if (Math.Abs(_cpuPercent - value) > 0.05) { _cpuPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(CpuText)); } }
    }

    public double CpuCorePercent
    {
        get => _cpuCorePercent;
        set { if (Math.Abs(_cpuCorePercent - value) > 0.05) { _cpuCorePercent = value; OnPropertyChanged(); } }
    }

    public double WorkingSetMb
    {
        get => _workingSetMb;
        set { if (Math.Abs(_workingSetMb - value) > 0.1) { _workingSetMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(MemoryText)); } }
    }

    public double PrivateMemoryMb
    {
        get => _privateMemoryMb;
        set { if (Math.Abs(_privateMemoryMb - value) > 0.1) { _privateMemoryMb = value; OnPropertyChanged(); } }
    }

    public double VirtualMemoryMb
    {
        get => _virtualMemoryMb;
        set { if (Math.Abs(_virtualMemoryMb - value) > 0.1) { _virtualMemoryMb = value; OnPropertyChanged(); } }
    }

    public int EstablishedClientConnections
    {
        get => _establishedClientConnections;
        set { if (_establishedClientConnections != value) { _establishedClientConnections = value; OnPropertyChanged(); } }
    }

    public int EstablishedInternalConnections
    {
        get => _establishedInternalConnections;
        set { if (_establishedInternalConnections != value) { _establishedInternalConnections = value; OnPropertyChanged(); } }
    }

    public int HandleCount
    {
        get => _handleCount;
        set { if (_handleCount != value) { _handleCount = value; OnPropertyChanged(); } }
    }

    public int ThreadCount
    {
        get => _threadCount;
        set { if (_threadCount != value) { _threadCount = value; OnPropertyChanged(); } }
    }

    public int? PortOwnerPid
    {
        get => _portOwnerPid;
        set { if (_portOwnerPid != value) { _portOwnerPid = value; OnPropertyChanged(); OnPropertyChanged(nameof(PortOwnerMatches)); } }
    }

    public DateTime? LastLogActivity
    {
        get => _lastLogActivity;
        set { if (_lastLogActivity != value) { _lastLogActivity = value; OnPropertyChanged(); } }
    }

    public bool PortOwnerMatches => PortOwnerPid is null || ProcessId is null || PortOwnerPid == ProcessId;
    public string CpuText => $"{CpuPercent:F1}%";
    public string MemoryText => WorkingSetMb <= 0 ? "-" : $"{WorkingSetMb:F0} MB";

    public string HealthSymbol => Health switch
    {
        HealthState.Healthy => "●",
        HealthState.Degraded => "▲",
        HealthState.Failed => "●",
        _ => "○"
    };

    public string PortSummary => ClientPort is null ? "-" : ClientPort.Value.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
