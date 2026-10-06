using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NextGen.Fiesta.ServerManager.Models;
using NextGen.Fiesta.ServerManager.Services;

namespace NextGen.Fiesta.ServerManager.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CommandRunner _runner = new();
    private readonly SettingsService _settingsService = new();
    private readonly FiestaTopologyScanner _scanner = new();
    private readonly NetworkInspector _network = new();
    private readonly HealthEngine _health = new();
    private readonly LogDiscoveryService _logDiscovery = new();
    private readonly LogAnalyzer _logAnalyzer = new();
    private readonly ConfigurationAuditor _configAuditor = new();
    private readonly ReferenceAuditService _referenceAudit = new();
    private readonly ProcessMetricsService _processMetrics = new();
    private readonly RuntimeHistoryService _runtimeHistory = new();
    private readonly LiveLogMonitorService _liveLog = new();
    private readonly IncidentCorrelationEngine _correlation = new();
    private readonly CapacityAuditService _capacityAudit = new();
    private readonly ClientTerrainAuditService _clientTerrainAudit = new();
    private readonly ZoneCapacityMonitor _zoneCapacityMonitor = new();
    private readonly PerformanceTuningAuditService _performanceTuningAudit = new();
    private readonly AdaptiveHookService _adaptiveHook = new();
    private readonly HardwareAdvisorService _hardwareAdvisor = new();
    private readonly DispatcherTimer _timer;
    private readonly WindowsServiceManager _serviceManager;
    private readonly ZoneLifecycleService _lifecycle;
    private readonly WindowsFirewallService _firewall;
    private readonly ZoneProvisioningService _zoneProvisioning;
    private readonly SmartOrchestrator _orchestrator;
    private readonly PdbSymbolService _pdb;
    private readonly WindowsEventLogService _eventLog;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private AppSettings _settings;
    private FiestaServiceEntry? _selectedService;
    private DiagnosticIssue? _selectedIssue;
    private string? _selectedLogPath;
    private string _logText = string.Empty;
    private string _activityText = string.Empty;
    private string _statusLine = "Bereit";
    private bool _isBusy;
    private int _healthScore;
    private bool _isAdministrator;
    private string _pdbSearch = string.Empty;
    private string _pdbOutput = string.Empty;
    private bool _isLiveMonitoring;
    private string _liveStatusText = "Live-Monitor: aus";
    private string _logInventoryText = "Keine Logquelle ausgewählt";
    private string _capacitySummary = "Noch keine Limit-/Map-Analyse ausgeführt";
    private string _clientTerrainSummary = "Noch keine Client-/Terrain-Analyse ausgeführt";
    private ClientTerrainAuditInfo _clientTerrainInfo = new();
    private string _zoneCapacitySummary = "Noch keine Live-Kapazitätsmessung";
    private string _scaleRecommendationText = "Noch keine Skalierungsbewertung";
    private ZoneProvisionPlan? _provisionPlan;
    private string _provisionStatus = "Noch kein neuer Zone-Plan erstellt";
    private WorldManagerCapacitySnapshot _worldManagerCapacity = new();
    private string _performanceTuningSummary = "Noch keine Performance-/Tuning-Analyse ausgeführt";
    private string _adaptiveHookSummary = "Noch keine Adaptive-Hook-Bewertung ausgeführt";
    private HardwareProfileSnapshot _hardwareProfile = new();

    public ObservableCollection<FiestaServiceEntry> Services { get; } = new();
    public ObservableCollection<DiagnosticIssue> Diagnostics { get; } = new();
    public ObservableCollection<string> LogFiles { get; } = new();
    public ObservableCollection<PdbStatus> PdbStatuses { get; } = new();
    public ObservableCollection<LogEvent> LiveEvents { get; } = new();
    public ObservableCollection<RuntimeTransition> RuntimeTransitions { get; } = new();
    public ObservableCollection<CapacityLimitEntry> CapacityLimits { get; } = new();
    public ObservableCollection<MapCapacityEntry> MapCapacities { get; } = new();
    public ObservableCollection<ClientTerrainProfileEntry> ClientTerrainProfiles { get; } = new();
    public ObservableCollection<ClientTerrainMapEntry> ClientTerrainMaps { get; } = new();
    public ObservableCollection<ZoneCapacitySnapshot> ZoneCapacities { get; } = new();
    public ObservableCollection<ProcessScalingSnapshot> ProcessScaling { get; } = new();
    public ObservableCollection<PerformanceTuningEntry> PerformanceTuningCandidates { get; } = new();
    public ObservableCollection<AdaptiveHookAssessment> AdaptiveHookAssessments { get; } = new();

    public AppSettings Settings { get => _settings; private set { _settings = value; OnPropertyChanged(); OnPropertyChanged(nameof(ServerRoot)); } }
    public string ServerRoot { get => Settings.ServerRoot; set { Settings.ServerRoot = value; OnPropertyChanged(); } }
    public bool IsAdministrator { get => _isAdministrator; private set { _isAdministrator = value; OnPropertyChanged(); OnPropertyChanged(nameof(AdminText)); } }
    public string AdminText => IsAdministrator ? "Administrator: JA" : "Administrator: NEIN – Service-Aktionen sind eingeschränkt";
    public int HealthScore { get => _healthScore; private set { _healthScore = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthText)); } }
    public string HealthText => $"Server Health {HealthScore}%";
    public string StatusLine { get => _statusLine; private set { _statusLine = value; OnPropertyChanged(); } }
    public string ActivityText { get => _activityText; private set { _activityText = value; OnPropertyChanged(); } }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); } }

    public FiestaServiceEntry? SelectedService
    {
        get => _selectedService;
        set
        {
            _selectedService = value;
            OnPropertyChanged();
            RefreshLogList();
            RaiseCommands();
        }
    }

    public DiagnosticIssue? SelectedIssue { get => _selectedIssue; set { _selectedIssue = value; OnPropertyChanged(); RaiseCommands(); } }

    public string? SelectedLogPath
    {
        get => _selectedLogPath;
        set { _selectedLogPath = value; OnPropertyChanged(); LoadSelectedLog(); RaiseCommands(); }
    }

    public string LogText { get => _logText; private set { _logText = value; OnPropertyChanged(); } }
    public string PdbSearch { get => _pdbSearch; set { _pdbSearch = value; OnPropertyChanged(); } }
    public string PdbOutput { get => _pdbOutput; private set { _pdbOutput = value; OnPropertyChanged(); } }
    public bool IsLiveMonitoring { get => _isLiveMonitoring; private set { _isLiveMonitoring = value; OnPropertyChanged(); } }
    public string LiveStatusText { get => _liveStatusText; private set { _liveStatusText = value; OnPropertyChanged(); } }
    public string LogInventoryText { get => _logInventoryText; private set { _logInventoryText = value; OnPropertyChanged(); } }
    public string CapacitySummary { get => _capacitySummary; private set { _capacitySummary = value; OnPropertyChanged(); } }
    public string ClientTerrainSummary { get => _clientTerrainSummary; private set { _clientTerrainSummary = value; OnPropertyChanged(); } }
    public ClientTerrainAuditInfo ClientTerrainInfo { get => _clientTerrainInfo; private set { _clientTerrainInfo = value; OnPropertyChanged(); } }
    public string ClientBinaryPath => ClientTerrainInfo.ClientBinaryPath;
    public string ZoneCapacitySummary { get => _zoneCapacitySummary; private set { _zoneCapacitySummary = value; OnPropertyChanged(); } }
    public string ScaleRecommendationText { get => _scaleRecommendationText; private set { _scaleRecommendationText = value; OnPropertyChanged(); } }
    public ZoneProvisionPlan? ProvisionPlan { get => _provisionPlan; private set { _provisionPlan = value; OnPropertyChanged(); } }
    public string ProvisionStatus { get => _provisionStatus; private set { _provisionStatus = value; OnPropertyChanged(); } }
    public WorldManagerCapacitySnapshot WorldManagerCapacity { get => _worldManagerCapacity; private set { _worldManagerCapacity = value; OnPropertyChanged(); } }
    public string PerformanceTuningSummary { get => _performanceTuningSummary; private set { _performanceTuningSummary = value; OnPropertyChanged(); } }
    public string AdaptiveHookSummary { get => _adaptiveHookSummary; private set { _adaptiveHookSummary = value; OnPropertyChanged(); } }
    public HardwareProfileSnapshot HardwareProfile { get => _hardwareProfile; private set { _hardwareProfile = value; OnPropertyChanged(); OnPropertyChanged(nameof(HardwareSummary)); OnPropertyChanged(nameof(HardwareAdvice)); OnPropertyChanged(nameof(HardwareScalingMode)); } }
    public string HardwareSummary => HardwareProfile.Summary;
    public string HardwareAdvice => HardwareProfile.Advice;
    public string HardwareScalingMode => HardwareProfile.ScalingMode;

    public int HookWmClientTarget { get => Settings.HookWorldManagerClientSessions; set { Settings.HookWorldManagerClientSessions = Math.Clamp(value, 1, 20000); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookWmZoneTarget { get => Settings.HookWorldManagerZoneSessions; set { Settings.HookWorldManagerZoneSessions = Math.Clamp(value, 1, 1000); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookZoneClientTarget { get => Settings.HookZoneClientSessions; set { Settings.HookZoneClientSessions = Math.Clamp(value, 1, 5000); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookZonePlayerTarget { get => Settings.HookZoneShinePlayer; set { Settings.HookZoneShinePlayer = Math.Clamp(value, 1, 10000); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookZoneMobTarget { get => Settings.HookZoneShineMob; set { Settings.HookZoneShineMob = Math.Clamp(value, 1, 50000); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookZoneNpcTarget { get => Settings.HookZoneShineNpc; set { Settings.HookZoneShineNpc = Math.Clamp(value, 1, 5000); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookCpuWarnTarget { get => Settings.HookCpuWarnPercent; set { Settings.HookCpuWarnPercent = Math.Clamp(value, 10, 99); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookCpuBlockTarget { get => Settings.HookCpuBlockPercent; set { Settings.HookCpuBlockPercent = Math.Clamp(value, HookCpuWarnTarget + 1, 100); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookMemoryWarnTarget { get => Settings.HookMemoryWarnPercent; set { Settings.HookMemoryWarnPercent = Math.Clamp(value, 10, 99); PersistHookSetting(); OnPropertyChanged(); } }
    public int HookMemoryBlockTarget { get => Settings.HookMemoryBlockPercent; set { Settings.HookMemoryBlockPercent = Math.Clamp(value, HookMemoryWarnTarget + 1, 100); PersistHookSetting(); OnPropertyChanged(); } }
    public bool AllowExperimentalZoneBinaryHooks { get => Settings.AllowExperimentalZoneBinaryHooks; set { Settings.AllowExperimentalZoneBinaryHooks = value; PersistHookSetting(); OnPropertyChanged(); } }

    public ICommand BrowseRootCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RunElevatedCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public AsyncRelayCommand ReinstallCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand ConfigureRecoveryCommand { get; }
    public AsyncRelayCommand SmartStartCommand { get; }
    public AsyncRelayCommand SmartStopCommand { get; }
    public AsyncRelayCommand SmartRestartCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand ApplyRepairCommand { get; }
    public AsyncRelayCommand IndexPdbCommand { get; }
    public ICommand SearchPdbCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenConfigCommand { get; }
    public ICommand StartLiveCommand { get; }
    public ICommand StopLiveCommand { get; }
    public ICommand ClearLiveCommand { get; }
    public ICommand ReloadLogCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand AnalyzeCapacityCommand { get; }
    public ICommand AnalyzeClientTerrainCommand { get; }
    public ICommand BrowseClientBinaryCommand { get; }
    public AsyncRelayCommand RefreshZoneCapacityCommand { get; }
    public AsyncRelayCommand PlanNewZoneCommand { get; }
    public AsyncRelayCommand CreateNewZoneCommand { get; }
    public ICommand AnalyzePerformanceTuningCommand { get; }
    public ICommand AnalyzeAdaptiveHooksCommand { get; }
    public AsyncRelayCommand ApplyAdaptiveHooksCommand { get; }
    public ICommand RestoreAdaptiveHooksCommand { get; }
    public ICommand SetHookProfileCommand { get; }

    public MainViewModel()
    {
        _serviceManager = new WindowsServiceManager(_runner);
        _lifecycle = new ZoneLifecycleService(_serviceManager, _runner);
        _firewall = new WindowsFirewallService(_runner);
        _zoneProvisioning = new ZoneProvisioningService(_serviceManager, _lifecycle, _firewall);
        _orchestrator = new SmartOrchestrator(_serviceManager, _network, _health);
        _pdb = new PdbSymbolService(_runner);
        _eventLog = new WindowsEventLogService(_runner);
        _liveLog.LineReceived += OnLiveLogLine;
        _orchestrator.Progress += e => System.Windows.Application.Current.Dispatcher.Invoke(() => AppendActivity($"[{e.Timestamp:HH:mm:ss}] {e.Message}"));
        _settings = _settingsService.Load();
        IsAdministrator = AdminService.IsAdministrator();

        BrowseRootCommand = new RelayCommand(_ => BrowseRoot());
        ScanCommand = new RelayCommand(_ => Scan());
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        RunElevatedCommand = new RelayCommand(_ => { if (AdminService.RestartElevated()) System.Windows.Application.Current.Shutdown(); });
        StartCommand = new AsyncRelayCommand(_ => ServiceActionAsync("start"), _ => SelectedService != null && IsAdministrator);
        StopCommand = new AsyncRelayCommand(_ => ServiceActionAsync("stop"), _ => SelectedService != null && IsAdministrator);
        RestartCommand = new AsyncRelayCommand(_ => ServiceActionAsync("restart"), _ => SelectedService != null && IsAdministrator);
        ReinstallCommand = new AsyncRelayCommand(_ => ReinstallAsync(), _ => SelectedService != null && IsAdministrator);
        UninstallCommand = new AsyncRelayCommand(_ => UninstallAsync(), _ => SelectedService != null && IsAdministrator);
        ConfigureRecoveryCommand = new AsyncRelayCommand(_ => ConfigureRecoveryAsync(), _ => SelectedService != null && IsAdministrator);
        SmartStartCommand = new AsyncRelayCommand(_ => RunOrchestrationAsync("start"), _ => Services.Count > 0 && IsAdministrator);
        SmartStopCommand = new AsyncRelayCommand(_ => RunOrchestrationAsync("stop"), _ => Services.Count > 0 && IsAdministrator);
        SmartRestartCommand = new AsyncRelayCommand(_ => RunOrchestrationAsync("restart"), _ => Services.Count > 0 && IsAdministrator);
        AnalyzeCommand = new AsyncRelayCommand(_ => AnalyzeAsync(), _ => Services.Count > 0);
        ApplyRepairCommand = new AsyncRelayCommand(_ => ApplyRepairAsync(), _ => SelectedIssue?.IsAutoRepairable == true && IsAdministrator);
        IndexPdbCommand = new AsyncRelayCommand(_ => IndexPdbAsync(), _ => Services.Count > 0);
        // Keep the search command enabled. Older builds created it disabled while PdbSearch was empty,
        // but never raised CanExecuteChanged when the user typed a term.
        SearchPdbCommand = new RelayCommand(_ => SearchPdb());
        OpenFolderCommand = new RelayCommand(_ => OpenSelectedFolder(), _ => SelectedService != null);
        OpenConfigCommand = new RelayCommand(_ => OpenSelectedConfig(), _ => SelectedService?.ConfigPath is not null);
        StartLiveCommand = new RelayCommand(_ => StartLiveMonitoring(), _ => Services.Count > 0 && !IsLiveMonitoring);
        StopLiveCommand = new RelayCommand(_ => StopLiveMonitoring(), _ => IsLiveMonitoring);
        ClearLiveCommand = new RelayCommand(_ => { LiveEvents.Clear(); RuntimeTransitions.Clear(); _runtimeHistory.Reset(); StatusLine = "Live-Timeline geleert"; });
        ReloadLogCommand = new RelayCommand(_ => LoadSelectedLog(), _ => !string.IsNullOrWhiteSpace(SelectedLogPath));
        ExportReportCommand = new RelayCommand(_ => ExportReport(), _ => Services.Count > 0);
        AnalyzeCapacityCommand = new RelayCommand(_ => AnalyzeCapacity());
        AnalyzeClientTerrainCommand = new RelayCommand(_ => AnalyzeClientTerrain());
        BrowseClientBinaryCommand = new RelayCommand(_ => BrowseClientBinary());
        RefreshZoneCapacityCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => Services.Any(x => x.Kind == FiestaServiceKind.Zone));
        PlanNewZoneCommand = new AsyncRelayCommand(_ => PlanNewZoneAsync(), _ => Services.Any(x => x.Kind == FiestaServiceKind.Zone));
        CreateNewZoneCommand = new AsyncRelayCommand(_ => CreateNewZoneAsync(), _ => IsAdministrator && ProvisionPlan?.IsValid == true);
        AnalyzePerformanceTuningCommand = new RelayCommand(_ => AnalyzePerformanceTuning());
        AnalyzeAdaptiveHooksCommand = new RelayCommand(_ => AnalyzeAdaptiveHooks());
        ApplyAdaptiveHooksCommand = new AsyncRelayCommand(_ => ApplyAdaptiveHooksAsync(), _ => IsAdministrator && Services.Count > 0);
        RestoreAdaptiveHooksCommand = new RelayCommand(_ => RestoreAdaptiveHooks(), _ => IsAdministrator && !string.IsNullOrWhiteSpace(ServerRoot));
        SetHookProfileCommand = new RelayCommand(p => SetHookProfile(p?.ToString()));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, Settings.RefreshIntervalSeconds)) };
        _timer.Tick += async (_, _) => await RefreshAsync(silent: true);
        _timer.Start();

        if (!string.IsNullOrWhiteSpace(ServerRoot) && _scanner.IsValidRoot(ServerRoot)) Scan();
    }

    private void BrowseRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "NA2016 Server-Ordner auswählen (enthält 9Data, WorldManager, Zone00 …)",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        ServerRoot = dialog.FolderName;
        Settings.ServerRoot = ServerRoot;
        _settingsService.Save(Settings);
        Scan();
    }

    private void Scan()
    {
        if (!_scanner.IsValidRoot(ServerRoot))
        {
            System.Windows.MessageBox.Show("Der ausgewählte Ordner sieht nicht wie der NA2016 Server-Root aus. Erwartet werden u.a. 9Data und WorldManager.", "NextGen Fiesta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsLiveMonitoring) StopLiveMonitoring();
        _runtimeHistory.Reset();
        RuntimeTransitions.Clear();
        Services.Clear();
        foreach (var s in _scanner.Scan(ServerRoot)) Services.Add(s);
        StatusLine = $"{Services.Count} Dienste/Zonen erkannt";
        Settings.ServerRoot = ServerRoot;
        _settingsService.Save(Settings);
        _ = RefreshAsync();
        _ = AnalyzeAsync();
        AnalyzeCapacity();
        AnalyzeClientTerrain();
        RefreshZoneCapacity();
        AnalyzePerformanceTuning();
        AnalyzeAdaptiveHooks();
        _ = PlanNewZoneAsync(silent: true);
        if (Settings.AutoAnalyzeLogs) StartLiveMonitoring();
        RaiseCommands();
    }

    private async Task RefreshAsync(bool silent = false)
    {
        if (Services.Count == 0 || !await _refreshLock.WaitAsync(0)) return;
        try
        {
            var portPidMap = await _network.GetListeningPidMapAsync(_runner);
            var trackedPorts = Services.SelectMany(x => new[] { x.ClientPort, x.InternalPort }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            var establishedCounts = _network.GetEstablishedConnectionCounts(trackedPorts);
            foreach (var s in Services)
            {
                var q = await _serviceManager.QueryAsync(s.ServiceName);
                s.State = q.State;
                s.ProcessId = q.Pid;
                s.ProcessStartTime = _serviceManager.GetProcessStartTime(q.Pid);
                s.PortOpen = s.ClientPort is int p && await _network.IsPortOpenAsync(p);
                s.PortOwnerPid = s.ClientPort is int cp && portPidMap.TryGetValue(cp, out var ownerPid) ? ownerPid : null;
                var metrics = _processMetrics.SampleProcess(q.Pid);
                s.CpuPercent = metrics?.CpuPercent ?? 0;
                s.CpuCorePercent = metrics?.CpuCorePercent ?? 0;
                s.WorkingSetMb = metrics?.WorkingSetMb ?? 0;
                s.PrivateMemoryMb = metrics?.PrivateMemoryMb ?? 0;
                s.VirtualMemoryMb = metrics?.VirtualMemoryMb ?? 0;
                s.EstablishedClientConnections = s.ClientPort is int livePort && establishedCounts.TryGetValue(livePort, out var liveCount) ? liveCount : 0;
                s.EstablishedInternalConnections = s.InternalPort is int internalPort && establishedCounts.TryGetValue(internalPort, out var internalCount) ? internalCount : 0;
                s.HandleCount = metrics?.HandleCount ?? 0;
                s.ThreadCount = metrics?.ThreadCount ?? 0;
                _health.Evaluate(s);

                var transition = _runtimeHistory.Observe(s, DateTime.Now);
                if (transition is not null)
                {
                    RuntimeTransitions.Insert(0, transition);
                    while (RuntimeTransitions.Count > 300) RuntimeTransitions.RemoveAt(RuntimeTransitions.Count - 1);
                }
            }
            HealthScore = _health.Score(Services);
            RefreshZoneCapacity();
            AnalyzePerformanceTuning(silent: true);
            AnalyzeAdaptiveHooks(silent: true);
            RefreshCorrelations();
            if (!silent) StatusLine = $"Status aktualisiert: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { if (!silent) StatusLine = ex.Message; }
        finally { _refreshLock.Release(); }
    }

    private async Task ServiceActionAsync(string action)
    {
        var svc = SelectedService!;
        IsBusy = true;
        try
        {
            CommandResult result = action switch
            {
                "start" => await _serviceManager.StartAsync(svc.ServiceName),
                "stop" => await _serviceManager.StopAsync(svc.ServiceName),
                _ => await _serviceManager.RestartAsync(svc.ServiceName)
            };
            AppendActivity($"{action.ToUpperInvariant()} {svc.ServiceName}: {(result.Success ? "OK" : result.StdErr + result.StdOut)}");
            await Task.Delay(700);
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    private async Task ReinstallAsync()
    {
        var svc = SelectedService!;
        if (System.Windows.MessageBox.Show($"Dienst {svc.ServiceName} stoppen, löschen und mit dem nativen NA2016-Mechanismus neu registrieren?\n\nDie Programmdateien werden NICHT gelöscht.", "Dienst neu installieren", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            var r = await _lifecycle.ReinstallAsync(svc, Settings.UseServiceRecovery);
            AppendActivity($"REINSTALL {svc.ServiceName}: {(r.Success ? "OK" : r.StdErr)}");
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    private async Task UninstallAsync()
    {
        var svc = SelectedService!;
        if (System.Windows.MessageBox.Show($"Windows-Dienst {svc.ServiceName} wirklich löschen?\n\nDateien im Serverordner bleiben erhalten.", "Dienst löschen", MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
        var r = await _lifecycle.UninstallServiceAsync(svc);
        AppendActivity($"UNINSTALL {svc.ServiceName}: {(r.Success ? "OK" : r.StdErr + r.StdOut)}");
        await RefreshAsync();
    }

    private async Task ConfigureRecoveryAsync()
    {
        var r = await _serviceManager.ConfigureRecoveryAsync(SelectedService!.ServiceName);
        AppendActivity($"RECOVERY {SelectedService!.ServiceName}: {(r.Success ? "OK" : r.StdErr + r.StdOut)}");
    }

    private async Task RunOrchestrationAsync(string mode)
    {
        IsBusy = true;
        AppendActivity($"--- SMART {mode.ToUpperInvariant()} ---");
        try
        {
            if (mode == "start") await _orchestrator.SmartStartAsync(Services.ToList(), Settings);
            else if (mode == "stop") await _orchestrator.SmartStopAsync(Services.ToList());
            else await _orchestrator.SmartRestartAsync(Services.ToList(), Settings);
        }
        catch (Exception ex)
        {
            AppendActivity("FEHLER: " + ex.Message);
            Diagnostics.Insert(0, new DiagnosticIssue
            {
                Code = "NG-START-0001",
                Severity = DiagnosticSeverity.Error,
                Title = "Smart-Start/Stop fehlgeschlagen",
                Description = ex.Message,
                Recommendation = "Betroffenen Dienst auswählen, Logs analysieren und Service-/Portstatus prüfen.",
                Source = "SmartOrchestrator",
                Confidence = 1.0
            });
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private void RefreshZoneCapacity()
    {
        var snapshots = _zoneCapacityMonitor.Build(Services.ToList(), MapCapacities.ToList(), Settings, Diagnostics.ToList());
        WorldManagerCapacity = _zoneCapacityMonitor.BuildWorldManager(Services.ToList(), Settings);
        ZoneCapacities.Clear();
        foreach (var x in snapshots) ZoneCapacities.Add(x);

        if (snapshots.Count == 0)
        {
            ZoneCapacitySummary = "Keine Zonen gefunden.";
            ScaleRecommendationText = "Keine Skalierungsbewertung möglich.";
            return;
        }

        var running = snapshots.Count(x => x.State.Equals(ServiceRuntimeState.Running.ToString(), StringComparison.OrdinalIgnoreCase));
        var clients = snapshots.Sum(x => x.ClientConnections);
        var hottest = snapshots.Where(x => x.State.Equals(ServiceRuntimeState.Running.ToString(), StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.OverallPercent).FirstOrDefault()
            ?? snapshots.OrderByDescending(x => x.OverallPercent).First();
        ZoneCapacitySummary = $"{running}/{snapshots.Count} Zonen laufen · {clients:N0} etablierte Client-Sessions · höchste messbare Last: {hottest.ZoneName} {hottest.OverallText} ({hottest.Pressure}, {hottest.Trend})";

        var urgent = snapshots.Where(x => x.Pressure is "KRITISCH" or "AUSBAU").OrderByDescending(x => x.OverallPercent).ToList();
        var warningRising = snapshots.Where(x => x.Pressure == "WARNUNG" && x.Trend.Contains("steigend", StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.OverallPercent).ToList();
        if (urgent.Count > 0)
            ScaleRecommendationText = $"Skalierung empfohlen: {string.Join(", ", urgent.Select(x => x.ZoneName))}. {urgent[0].Recommendation}";
        else if (warningRising.Count > 0)
            ScaleRecommendationText = $"Frühwarnung: {warningRising[0].ZoneName} steigt an. {warningRising[0].Recommendation}";
        else
            ScaleRecommendationText = "Aktuell keine neue Zone aus den messbaren Schwellen erforderlich. Player-/TCP-Sessions, CPU-Core-Druck, Privat-RAM und konfigurierte Maps werden weiter überwacht.";
    }

    private void AnalyzePerformanceTuning(bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(ServerRoot) || !Directory.Exists(ServerRoot))
        {
            PerformanceTuningSummary = "Server Root nicht verfügbar";
            return;
        }

        try
        {
            HardwareProfile = _hardwareAdvisor.Analyze(Services.ToList());
            var result = _performanceTuningAudit.Analyze(ServerRoot, Services.ToList());
            ProcessScaling.Clear();
            foreach (var x in result.Processes) ProcessScaling.Add(x);
            PerformanceTuningCandidates.Clear();
            foreach (var x in result.Candidates) PerformanceTuningCandidates.Add(x);
            PerformanceTuningSummary = $"{result.Summary} Host: {HardwareProfile.ScalingMode}.";
            if (!silent) StatusLine = $"Performance-/Tuning-Audit: {ProcessScaling.Count} Kernkomponenten · {PerformanceTuningCandidates.Count} Kandidaten";
        }
        catch (Exception ex)
        {
            PerformanceTuningSummary = "Performance-/Tuning-Analyse fehlgeschlagen: " + ex.Message;
            if (!silent) StatusLine = PerformanceTuningSummary;
        }
    }

    private void PersistHookSetting()
    {
        _settingsService.Save(Settings);
        AnalyzeAdaptiveHooks(silent: true);
    }

    private void SetHookProfile(string? profile)
    {
        switch (profile?.Trim().ToLowerInvariant())
        {
            case "safe":
                Settings.HookWorldManagerClientSessions = 2000;
                Settings.HookWorldManagerZoneSessions = 125;
                Settings.HookZoneClientSessions = 1500;
                Settings.HookZoneShinePlayer = 1500;
                Settings.HookZoneShineMob = 8000;
                Settings.HookZoneShineNpc = 256;
                Settings.HookCpuWarnPercent = 65; Settings.HookCpuBlockPercent = 85;
                Settings.HookMemoryWarnPercent = 70; Settings.HookMemoryBlockPercent = 85;
                Settings.AllowExperimentalZoneBinaryHooks = false;
                break;
            case "balanced":
                Settings.HookWorldManagerClientSessions = 3000;
                Settings.HookWorldManagerZoneSessions = 150;
                Settings.HookZoneClientSessions = 1500;
                Settings.HookZoneShinePlayer = 2000;
                Settings.HookZoneShineMob = 12000;
                Settings.HookZoneShineNpc = 512;
                Settings.HookCpuWarnPercent = 70; Settings.HookCpuBlockPercent = 90;
                Settings.HookMemoryWarnPercent = 75; Settings.HookMemoryBlockPercent = 90;
                Settings.AllowExperimentalZoneBinaryHooks = false;
                break;
            case "high":
                Settings.HookWorldManagerClientSessions = 5000;
                Settings.HookWorldManagerZoneSessions = 200;
                Settings.HookZoneClientSessions = 1500;
                Settings.HookZoneShinePlayer = 2500;
                Settings.HookZoneShineMob = 16000;
                Settings.HookZoneShineNpc = 768;
                Settings.HookCpuWarnPercent = 75; Settings.HookCpuBlockPercent = 95;
                Settings.HookMemoryWarnPercent = 80; Settings.HookMemoryBlockPercent = 93;
                Settings.AllowExperimentalZoneBinaryHooks = true;
                break;
            default:
                return;
        }
        _settingsService.Save(Settings);
        OnPropertyChanged(nameof(HookWmClientTarget));
        OnPropertyChanged(nameof(HookWmZoneTarget));
        OnPropertyChanged(nameof(HookZoneClientTarget));
        OnPropertyChanged(nameof(HookZonePlayerTarget));
        OnPropertyChanged(nameof(HookZoneMobTarget));
        OnPropertyChanged(nameof(HookZoneNpcTarget));
        OnPropertyChanged(nameof(HookCpuWarnTarget)); OnPropertyChanged(nameof(HookCpuBlockTarget));
        OnPropertyChanged(nameof(HookMemoryWarnTarget)); OnPropertyChanged(nameof(HookMemoryBlockTarget));
        OnPropertyChanged(nameof(AllowExperimentalZoneBinaryHooks));
        AnalyzeAdaptiveHooks();
        StatusLine = $"Hook-Profil '{profile}' geladen – noch nicht angewendet.";
    }

    private void AnalyzeAdaptiveHooks(bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(ServerRoot) || !Directory.Exists(ServerRoot))
        {
            AdaptiveHookSummary = "Server Root nicht verfügbar";
            return;
        }
        try
        {
            var result = _adaptiveHook.Analyze(ServerRoot, Services.ToList(), Settings);
            AdaptiveHookAssessments.Clear();
            foreach (var item in result.Assessments) AdaptiveHookAssessments.Add(item);
            AdaptiveHookSummary = result.Summary;
            if (!silent) StatusLine = $"Adaptive-Hook-Audit: {AdaptiveHookAssessments.Count} Ressourcen bewertet";
        }
        catch (Exception ex)
        {
            AdaptiveHookSummary = "Adaptive-Hook-Audit fehlgeschlagen: " + ex.Message;
            if (!silent) StatusLine = AdaptiveHookSummary;
        }
    }

    private async Task ApplyAdaptiveHooksAsync()
    {
        AnalyzeAdaptiveHooks(silent: true);
        var canApply = AdaptiveHookAssessments.Where(x => x.CanApply).ToList();
        if (canApply.Count == 0)
        {
            System.Windows.MessageBox.Show("Aktuell ist kein Hook freigegeben. Prüfe CPU/RAM, Baseline-Hash und Zielwerte.", "Adaptive Hooks", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var changes = string.Join("\n", canApply.Select(x => $"• {x.Scope} / {x.Resource}: {x.CurrentText} → {x.TargetText} ({x.Decision})"));
        var blocked = AdaptiveHookAssessments.Where(x => !x.CanApply && x.HookType.Contains("BINARY", StringComparison.OrdinalIgnoreCase)).ToList();
        var blockedText = blocked.Count > 0
            ? $"\n\n{blocked.Count} Zone-Binary-Pool-Hook(s) bleiben aus Sicherheitsgründen gesperrt und werden NICHT geschrieben."
            : string.Empty;
        var answer = System.Windows.MessageBox.Show(
            $"Folgende freigegebenen Hook-Änderungen anwenden?\n\n{changes}{blockedText}\n\nServerInfo wird vorher gesichert. Ein WM-Hard-Pool-Change benötigt einen Restart; der live g_UserLimit-Hook wird nur beim exakten Baseline-Build und nur innerhalb m_MaxSessions gesetzt.",
            "Adaptive Hooks anwenden", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var result = await _adaptiveHook.ApplySafeHooksAsync(ServerRoot, Services.ToList(), Settings);
            StatusLine = result.Summary;
            AppendActivity($"[{DateTime.Now:HH:mm:ss}] Adaptive Hooks: {result.Summary}");
            if (result.Success && result.RestartRequired)
                System.Windows.MessageBox.Show(result.Summary + "\n\nFür Hard-Pool-/Listener-Änderungen bitte anschließend Smart Restart verwenden und danach den Hook-Audit erneut prüfen.", "Adaptive Hooks", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (!result.Success)
                System.Windows.MessageBox.Show(result.Summary, "Adaptive Hooks", MessageBoxButton.OK, MessageBoxImage.Error);
            AnalyzeAdaptiveHooks();
        }
        finally { IsBusy = false; RaiseCommands(); }
    }

    private void RestoreAdaptiveHooks()
    {
        var answer = System.Windows.MessageBox.Show(
            "Letztes Adaptive-Hook-Backup (ServerInfo.txt) wiederherstellen? Die Serverkomponenten müssen danach neu gestartet werden.",
            "Hook-Backup wiederherstellen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        var result = _adaptiveHook.RestoreLatest(ServerRoot);
        StatusLine = result.Summary;
        AppendActivity($"[{DateTime.Now:HH:mm:ss}] Hook-Restore: {result.Summary}");
        AnalyzeAdaptiveHooks();
    }

    private async Task PlanNewZoneAsync(bool silent = false)
    {
        try
        {
            ProvisionPlan = await _zoneProvisioning.CreatePlanAsync(ServerRoot, Services.ToList());
            ProvisionStatus = ProvisionPlan.IsValid
                ? $"Plan bereit: {ProvisionPlan.ZoneName} aus {ProvisionPlan.SourceZoneName} · {ProvisionPlan.PortsText} · Firewall {ProvisionPlan.FirewallText}."
                : $"Plan nicht bereit: {ProvisionPlan.Detail}";
            if (!silent) StatusLine = ProvisionStatus;
        }
        catch (Exception ex)
        {
            ProvisionPlan = null;
            ProvisionStatus = "Zone-Plan fehlgeschlagen: " + ex.Message;
            if (!silent) StatusLine = ProvisionStatus;
        }
        RaiseCommands();
    }

    private async Task CreateNewZoneAsync()
    {
        var plan = ProvisionPlan;
        if (plan is null || !plan.IsValid) return;
        var message = $"{plan.ZoneName} jetzt anlegen?\n\n" +
                      $"Template: {plan.SourceZoneName}\n" +
                      $"Ports: {plan.PortsText}\n" +
                      $"Firewall: {plan.FirewallText}\n\n" +
                      "Der Manager sichert ServerInfo.txt, klont die Quellzone ohne Logs, schreibt MY_SERVER/ServerInfo, registriert den Windows-Dienst und öffnet NUR den Client-Port eingehend.\n\n" +
                      "Die neue Zone wird absichtlich NICHT gestartet und es werden KEINE Maps automatisch verschoben.";
        if (System.Windows.MessageBox.Show(message, "Neue Fiesta-Zone provisionieren", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var result = await _zoneProvisioning.ProvisionAsync(ServerRoot, plan, Settings.UseServiceRecovery);
            ProvisionStatus = result.Summary + (string.IsNullOrWhiteSpace(result.BackupDirectory) ? string.Empty : $" Backup: {result.BackupDirectory}");
            AppendActivity($"ZONE PROVISION {plan.ZoneName}: {(result.Success ? "OK" : "FEHLER")} · {ProvisionStatus}");
            if (result.Success)
            {
                StatusLine = $"{plan.ZoneName} erfolgreich angelegt. Maps müssen anschließend bewusst zugewiesen werden.";
                Scan();
            }
            else
            {
                StatusLine = "Zone-Provisionierung fehlgeschlagen und wurde soweit möglich zurückgerollt.";
                System.Windows.MessageBox.Show(ProvisionStatus, "Provisionierung fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            IsBusy = false;
            RaiseCommands();
        }
    }

    private void BrowseClientBinary()
    {
        var dialog = new OpenFileDialog
        {
            Title = "NA2016 Client Fiesta.bin auswählen",
            Filter = "Fiesta Client (Fiesta.bin)|Fiesta.bin|BIN-Dateien (*.bin)|*.bin|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(Settings.ClientBinaryPath) && File.Exists(Settings.ClientBinaryPath))
            dialog.InitialDirectory = Path.GetDirectoryName(Settings.ClientBinaryPath);
        if (dialog.ShowDialog() != true) return;
        Settings.ClientBinaryPath = dialog.FileName;
        _settingsService.Save(Settings);
        AnalyzeClientTerrain();
    }

    private void AnalyzeClientTerrain()
    {
        if (string.IsNullOrWhiteSpace(ServerRoot) || !Directory.Exists(ServerRoot))
        {
            ClientTerrainSummary = "Server Root nicht verfügbar";
            return;
        }

        try
        {
            var result = _clientTerrainAudit.Analyze(ServerRoot, Settings.ClientBinaryPath);
            ClientTerrainInfo = result.Client;
            OnPropertyChanged(nameof(ClientBinaryPath));
            if (result.Client.Exists && !string.Equals(Settings.ClientBinaryPath, result.Client.ClientBinaryPath, StringComparison.OrdinalIgnoreCase))
            {
                Settings.ClientBinaryPath = result.Client.ClientBinaryPath;
                _settingsService.Save(Settings);
            }
            ClientTerrainProfiles.Clear();
            foreach (var x in result.Profiles) ClientTerrainProfiles.Add(x);
            ClientTerrainMaps.Clear();
            foreach (var x in result.Maps) ClientTerrainMaps.Add(x);
            ClientTerrainSummary = result.Summary;
            StatusLine = $"Client-/Terrain-Audit: {ClientTerrainInfo.BaselineText} · {ClientTerrainProfiles.Count} Größenprofile · {ClientTerrainMaps.Count} Client-Maps";
        }
        catch (Exception ex)
        {
            ClientTerrainSummary = "Client-/Terrain-Analyse fehlgeschlagen: " + ex.Message;
        }
    }

    private void AnalyzeCapacity()
    {
        if (string.IsNullOrWhiteSpace(ServerRoot) || !Directory.Exists(ServerRoot))
        {
            CapacitySummary = "Server Root nicht verfügbar";
            return;
        }

        try
        {
            var result = _capacityAudit.Analyze(ServerRoot);
            CapacityLimits.Clear();
            foreach (var x in result.Limits) CapacityLimits.Add(x);
            MapCapacities.Clear();
            foreach (var x in result.Maps) MapCapacities.Add(x);
            CapacitySummary = result.Summary;
            StatusLine = $"Limit-/Map-Analyse: {CapacityLimits.Count} Limits · {MapCapacities.Count} Karten/SHBD-Einträge";
        }
        catch (Exception ex)
        {
            CapacitySummary = "Limit-/Map-Analyse fehlgeschlagen: " + ex.Message;
        }
    }

    private async Task AnalyzeAsync()
    {
        await Task.Yield();
        Diagnostics.Clear();
        var discoveredLogFiles = 0;
        var analyzedLogFiles = 0;
        var analyzedLogLines = 0;
        foreach (var issue in _configAuditor.Audit(ServerRoot, Services)) Diagnostics.Add(issue);
        foreach (var issue in _referenceAudit.Audit(ServerRoot, Services.ToList())) Diagnostics.Add(issue);
        foreach (var svc in Services)
        {
            var logs = _logDiscovery.FindLogs(svc.DirectoryPath, Math.Max(1, Settings.LogScanMaxDepth));
            discoveredLogFiles += logs.Count;
            foreach (var path in logs.Take(Math.Max(1, Settings.MaxLogsPerServiceForAnalysis)))
            {
                var lines = _logDiscovery.Tail(path, Math.Max(100, Settings.LogTailLines));
                analyzedLogFiles++;
                analyzedLogLines += lines.Count;
                foreach (var issue in _logAnalyzer.Analyze(lines, path, svc.ServiceName, svc.ZoneNumber))
                    if (!Diagnostics.Any(x => x.Code == issue.Code && x.ServiceName == issue.ServiceName && x.Evidence == issue.Evidence)) Diagnostics.Add(issue);
            }

            if (svc.State == ServiceRuntimeState.Missing)
                Diagnostics.Add(new DiagnosticIssue { Code = "NG-SVC-0001", Severity = DiagnosticSeverity.Error, Title = "Windows-Dienst fehlt", Description = $"{svc.ServiceName} ist nicht im Service Control Manager registriert.", Recommendation = "Neu installieren verwenden.", Source = svc.DirectoryPath, ServiceName = svc.ServiceName, ZoneNumber = svc.ZoneNumber, RepairAction = RepairActionKind.ReinstallService, Confidence = 1.0 });
            else if (svc.State == ServiceRuntimeState.Stopped)
                Diagnostics.Add(new DiagnosticIssue { Code = svc.Kind == FiestaServiceKind.Zone ? "NG-ZONE-0002" : "NG-SVC-0002", Severity = DiagnosticSeverity.Error, Title = "Dienst ist gestoppt", Description = $"{svc.DisplayName} ist aktuell STOPPED.", Recommendation = "Logs auf den letzten Fehler prüfen und Dienst neu starten.", Source = svc.DirectoryPath, ServiceName = svc.ServiceName, ZoneNumber = svc.ZoneNumber, RepairAction = RepairActionKind.RestartService, Confidence = 1.0 });
        }
        foreach (var issue in await _eventLog.AnalyzeRecentAsync(TimeSpan.FromHours(6)))
            AddDiagnosticIfNew(issue);

        foreach (var issue in _correlation.Correlate(LiveEvents.ToList(), _runtimeHistory.Snapshot(TimeSpan.FromMinutes(15)), Services.ToList()))
            AddDiagnosticIfNew(issue);

        var ordered = Diagnostics.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Timestamp).ToList();
        Diagnostics.Clear(); foreach (var x in ordered) Diagnostics.Add(x);
        StatusLine = $"Diagnose abgeschlossen: {Diagnostics.Count} Hinweise · {analyzedLogFiles}/{discoveredLogFiles} Logs · {analyzedLogLines:N0} Zeilen geprüft";
        RaiseCommands();
    }

    private async Task ApplyRepairAsync()
    {
        var issue = SelectedIssue!;
        var svc = Services.FirstOrDefault(x => x.ServiceName == issue.ServiceName);
        try
        {
            switch (issue.RepairAction)
            {
                case RepairActionKind.StartService:
                    if (svc != null) await _serviceManager.StartAsync(svc.ServiceName);
                    break;
                case RepairActionKind.RestartService:
                    if (svc != null) await _serviceManager.RestartAsync(svc.ServiceName);
                    break;
                case RepairActionKind.ReinstallService:
                    if (svc != null && System.Windows.MessageBox.Show($"{svc.ServiceName} neu installieren?", "Auto-Reparatur", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        await _lifecycle.ReinstallAsync(svc, Settings.UseServiceRecovery);
                    break;
                case RepairActionKind.FixZoneServerInfo:
                    if (svc != null && svc.Kind == FiestaServiceKind.Zone && System.Windows.MessageBox.Show($"ZoneServerInfo von {svc.DisplayName} sichern und automatisch korrigieren?", "Auto-Reparatur", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        _configAuditor.FixZoneServerInfo(svc);
                    break;
                case RepairActionKind.ConfigureRecovery:
                    if (svc != null) await _serviceManager.ConfigureRecoveryAsync(svc.ServiceName);
                    break;
                case RepairActionKind.SmartStart:
                    await _orchestrator.SmartStartAsync(Services.ToList(), Settings);
                    break;
            }
            AppendActivity($"REPAIR {issue.Code}: ausgeführt");
            await RefreshAsync();
            await AnalyzeAsync();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Reparatur fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RefreshLogList()
    {
        LogFiles.Clear();
        if (SelectedService is null)
        {
            SelectedLogPath = null;
            LogInventoryText = "Keine Logquelle ausgewählt";
            return;
        }

        var logs = _logDiscovery.FindLogs(SelectedService.DirectoryPath, Math.Max(1, Settings.LogScanMaxDepth));
        foreach (var path in logs) LogFiles.Add(path);
        LogInventoryText = $"{logs.Count} Logdatei(en) rekursiv erkannt";
        SelectedLogPath = LogFiles.FirstOrDefault();
    }

    private void LoadSelectedLog()
    {
        if (string.IsNullOrWhiteSpace(SelectedLogPath)) { LogText = string.Empty; return; }
        LogText = string.Join(Environment.NewLine, _logDiscovery.Tail(SelectedLogPath, 1200));
    }

    private void ExportReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "NextGen Diagnosebericht speichern",
            Filter = "JSON Diagnosebericht (*.json)|*.json|Alle Dateien (*.*)|*.*",
            FileName = $"NextGen-Fiesta-Diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog() != true) return;

        var report = new
        {
            schema = "nextgen-fiesta-diagnostics/0.3",
            generatedAt = DateTime.Now,
            serverRoot = ServerRoot,
            healthScore = HealthScore,
            services = Services.Select(x => new
            {
                x.DisplayName,
                x.ServiceName,
                kind = x.Kind.ToString(),
                x.ZoneNumber,
                state = x.State.ToString(),
                health = x.Health.ToString(),
                x.StatusText,
                x.ProcessId,
                x.ProcessStartTime,
                x.ClientPort,
                x.InternalPort,
                x.OptoolPort,
                x.PortOpen,
                x.PortOwnerPid,
                x.CpuPercent,
                x.CpuCorePercent,
                x.WorkingSetMb,
                x.PrivateMemoryMb,
                x.VirtualMemoryMb,
                x.EstablishedClientConnections,
                x.EstablishedInternalConnections,
                x.HandleCount,
                x.ThreadCount,
                x.LastLogActivity,
                x.ExecutablePath,
                x.ConfigPath
            }),
            capacity = new
            {
                summary = CapacitySummary,
                limits = CapacityLimits.ToList(),
                maps = MapCapacities.ToList()
            },
            clientTerrain = new
            {
                summary = ClientTerrainSummary,
                client = ClientTerrainInfo,
                profiles = ClientTerrainProfiles.ToList(),
                maps = ClientTerrainMaps.ToList()
            },
            liveZoneCapacity = new
            {
                summary = ZoneCapacitySummary,
                recommendation = ScaleRecommendationText,
                zones = ZoneCapacities.ToList(),
                worldManager = WorldManagerCapacity,
                provisionPlan = ProvisionPlan,
                provisionStatus = ProvisionStatus
            },
            performanceTuning = new
            {
                summary = PerformanceTuningSummary,
                processes = ProcessScaling.ToList(),
                candidates = PerformanceTuningCandidates.ToList()
            },
            adaptiveHooks = new
            {
                summary = AdaptiveHookSummary,
                assessments = AdaptiveHookAssessments.ToList(),
                profile = new
                {
                    Settings.HookWorldManagerClientSessions,
                    Settings.HookWorldManagerZoneSessions,
                    Settings.HookZoneClientSessions,
                    Settings.HookZoneShinePlayer,
                    Settings.HookZoneShineMob,
                    Settings.HookZoneShineNpc,
                    Settings.HookCpuWarnPercent,
                    Settings.HookCpuBlockPercent,
                    Settings.HookMemoryWarnPercent,
                    Settings.HookMemoryBlockPercent,
                    Settings.AllowExperimentalZoneBinaryHooks
                }
            },
            diagnostics = Diagnostics.ToList(),
            runtimeTransitions = RuntimeTransitions.Take(300).ToList(),
            liveEvents = LiveEvents.Take(500).ToList(),
            logDiscovery = new
            {
                Settings.LogScanMaxDepth,
                Settings.MaxLogsPerServiceForAnalysis,
                Settings.LogTailLines,
                selectedServiceLogCount = SelectedService is null ? 0 : _logDiscovery.FindLogs(SelectedService.DirectoryPath, Settings.LogScanMaxDepth).Count
            }
        };

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            StatusLine = $"Diagnosebericht gespeichert: {dialog.FileName}";
            AppendActivity($"REPORT exportiert: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Export fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartLiveMonitoring()
    {
        if (Services.Count == 0 || IsLiveMonitoring) return;
        var targets = Services
            .Select(s => (s.ServiceName, s.ZoneNumber, s.DirectoryPath))
            .ToList();
        _liveLog.Start(targets);
        IsLiveMonitoring = true;
        LiveStatusText = $"Live-Monitor: aktiv · {_liveLog.WatchedFileCount} Dateien";
        AppendActivity($"LIVE MONITOR gestartet ({_liveLog.WatchedFileCount} Logs)");
        RaiseCommands();
    }

    private void StopLiveMonitoring()
    {
        _liveLog.Stop();
        IsLiveMonitoring = false;
        LiveStatusText = "Live-Monitor: aus";
        AppendActivity("LIVE MONITOR gestoppt");
        RaiseCommands();
    }

    private void OnLiveLogLine(LiveLogLine line)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var service = Services.FirstOrDefault(x => x.ServiceName.Equals(line.ServiceName, StringComparison.OrdinalIgnoreCase));
            if (service is not null) service.LastLogActivity = line.Timestamp;

            var evt = _logAnalyzer.ParseEvent(line.Line, line.ServiceName);
            LiveEvents.Insert(0, evt);
            while (LiveEvents.Count > 1500) LiveEvents.RemoveAt(LiveEvents.Count - 1);

            if (!string.IsNullOrWhiteSpace(SelectedLogPath) && SelectedLogPath.Equals(line.Path, StringComparison.OrdinalIgnoreCase))
            {
                var text = evt.Message;
                LogText = string.IsNullOrWhiteSpace(LogText) ? text : LogText + Environment.NewLine + text;
                if (LogText.Length > 250_000) LogText = LogText[^200_000..];
            }

            foreach (var issue in _logAnalyzer.Analyze(new[] { line.Line }, line.Path, line.ServiceName, line.ZoneNumber))
                AddDiagnosticIfNew(issue);

            if (evt.Severity >= DiagnosticSeverity.Warning)
                RefreshCorrelations();
        }));
    }

    private void RefreshCorrelations()
    {
        foreach (var issue in _correlation.Correlate(LiveEvents.ToList(), _runtimeHistory.Snapshot(TimeSpan.FromMinutes(15)), Services.ToList()))
            AddDiagnosticIfNew(issue);
    }

    private void AddDiagnosticIfNew(DiagnosticIssue issue)
    {
        var exists = Diagnostics.Any(x => x.Code == issue.Code
            && string.Equals(x.ServiceName, issue.ServiceName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Source, issue.Source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Evidence, issue.Evidence, StringComparison.Ordinal));
        if (!exists) Diagnostics.Insert(0, issue);
        while (Diagnostics.Count > 800) Diagnostics.RemoveAt(Diagnostics.Count - 1);
    }

    private async Task IndexPdbAsync()
    {
        PdbStatuses.Clear();
        var cache = Path.Combine(ServerRoot, ".nextgen-cache", "pdb");

        var pdbPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Services.Select(x => x.PdbPath).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>())
            if (File.Exists(path)) pdbPaths.Add(Path.GetFullPath(path));

        // Also discover PDBs stored centrally or in non-standard subfolders below the server root.
        // This is important for WorldManager.pdb and shared PDB collections.
        try
        {
            if (Directory.Exists(ServerRoot))
            {
                foreach (var path in Directory.EnumerateFiles(ServerRoot, "*.pdb", SearchOption.AllDirectories))
                {
                    if (path.Contains(Path.DirectorySeparatorChar + ".nextgen-cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    pdbPaths.Add(Path.GetFullPath(path));
                }
            }
        }
        catch (Exception ex)
        {
            PdbOutput = $"Hinweis: Rekursive PDB-Suche konnte nicht vollständig ausgeführt werden: {ex.Message}";
        }

        if (pdbPaths.Count == 0)
        {
            PdbOutput = "Keine PDB-Dateien unter dem Server-Root gefunden.";
            StatusLine = "Keine PDBs gefunden";
            return;
        }

        foreach (var pdbPath in pdbPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            PdbStatuses.Add(await _pdb.IndexAsync(pdbPath, cache));

        var ok = PdbStatuses.Count(x => x.Indexed);
        PdbOutput = $"PDB-Indexierung abgeschlossen: {ok}/{PdbStatuses.Count} indexiert.\r\nSuchbegriff eingeben und 'Symbole suchen' klicken oder Enter drücken.";
        StatusLine = $"PDB-Indexierung abgeschlossen ({ok}/{PdbStatuses.Count})";
    }

    private void SearchPdb()
    {
        var term = PdbSearch?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            PdbOutput = "Bitte einen Symbolnamen eingeben, z.B. CParserZone, WorldManagerSession, GUILDWARSTATUS oder MAX_KINGDOM.";
            return;
        }

        var cache = Path.Combine(ServerRoot, ".nextgen-cache", "pdb");
        if (!Directory.Exists(cache))
        {
            PdbOutput = "Noch kein PDB-Index vorhanden. Erst 'PDBs indexieren' ausführen.";
            return;
        }

        var indexFiles = Directory.EnumerateFiles(cache, "*.symbols.txt").ToList();
        if (indexFiles.Count == 0)
        {
            PdbOutput = "Der PDB-Cache-Ordner existiert, enthält aber keine Symbolindizes. PDBs erneut indexieren und die Statusspalte prüfen.";
            return;
        }

        var lines = new List<string>();
        var totalMatches = 0;
        foreach (var file in indexFiles)
        {
            var matches = _pdb.SearchIndex(file, term, 50).ToList();
            if (matches.Count == 0) continue;
            totalMatches += matches.Count;
            lines.Add($"=== {Path.GetFileName(file)} ({matches.Count} Treffer) ===");
            lines.AddRange(matches);
            lines.Add(string.Empty);
        }

        PdbOutput = totalMatches == 0
            ? $"Keine Treffer für '{term}'. Durchsucht: {indexFiles.Count} Symbolindizes."
            : $"Treffer für '{term}': {totalMatches} in {indexFiles.Count} Symbolindizes.\r\n\r\n" + string.Join(Environment.NewLine, lines);
    }

    private void OpenSelectedFolder()
    {
        if (SelectedService is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SelectedService.DirectoryPath}\"") { UseShellExecute = true });
    }

    private void OpenSelectedConfig()
    {
        if (SelectedService?.ConfigPath is not string path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void AppendActivity(string text) => ActivityText = string.IsNullOrWhiteSpace(ActivityText) ? text : ActivityText + Environment.NewLine + text;

    private void RaiseCommands()
    {
        StartCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); RestartCommand.RaiseCanExecuteChanged();
        ReinstallCommand.RaiseCanExecuteChanged(); UninstallCommand.RaiseCanExecuteChanged(); ConfigureRecoveryCommand.RaiseCanExecuteChanged();
        SmartStartCommand.RaiseCanExecuteChanged(); SmartStopCommand.RaiseCanExecuteChanged(); SmartRestartCommand.RaiseCanExecuteChanged();
        AnalyzeCommand.RaiseCanExecuteChanged(); ApplyRepairCommand.RaiseCanExecuteChanged(); IndexPdbCommand.RaiseCanExecuteChanged();
        (StartLiveCommand as RelayCommand)?.RaiseCanExecuteChanged(); (StopLiveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ReloadLogCommand as RelayCommand)?.RaiseCanExecuteChanged(); (ExportReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RefreshZoneCapacityCommand.RaiseCanExecuteChanged(); PlanNewZoneCommand.RaiseCanExecuteChanged(); CreateNewZoneCommand.RaiseCanExecuteChanged();
        ApplyAdaptiveHooksCommand.RaiseCanExecuteChanged(); (RestoreAdaptiveHooksCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _timer.Stop();
        _liveLog.LineReceived -= OnLiveLogLine;
        _liveLog.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
