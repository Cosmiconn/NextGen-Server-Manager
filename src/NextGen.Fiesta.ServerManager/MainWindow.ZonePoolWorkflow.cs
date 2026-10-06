using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NextGen.Fiesta.ServerManager.Services;
using NextGen.Fiesta.ServerManager.ViewModels;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private const string ZonePoolDeployConfirmation = "DEPLOY-CERTIFIED-ZONE-POOL-2000-12000-512";
    private const string ZonePoolRollbackConfirmation = "ROLLBACK-CERTIFIED-ZONE-POOL-TEST";
    private static readonly ConditionalWeakTable<MainWindow, object> ZonePoolWorkflowAttached = new();
    private static readonly bool ZonePoolWorkflowLoadedHookRegistered = RegisterZonePoolWorkflowLoadedHook();

    private ComboBox? _zonePoolTargetCombo;
    private TextBlock? _zonePoolWorkflowStatus;
    private string? _zonePoolCheckpointPath;
    private bool _zonePoolWorkflowBusy;
    private int _zonePoolAttachAttempts;

    private static bool RegisterZonePoolWorkflowLoadedHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnZonePoolWorkflowWindowLoaded));
        return true;
    }

    private static void OnZonePoolWorkflowWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || ZonePoolWorkflowAttached.TryGetValue(window, out _))
            return;

        window.Dispatcher.BeginInvoke(new Action(window.TryAttachZonePoolWorkflow), DispatcherPriority.ContextIdle);
    }

    private void TryAttachZonePoolWorkflow()
    {
        if (ZonePoolWorkflowAttached.TryGetValue(this, out _)) return;

        _zonePoolAttachAttempts++;
        if (_mainNavigation?.Items.Count < 2 ||
            _mainNavigation.Items[1] is not TabItem serverTab ||
            serverTab.Content is not TabControl serverNavigation ||
            serverNavigation.Items.Count < 3 ||
            serverNavigation.Items[2] is not TabItem adaptive ||
            adaptive.Content is not Grid root ||
            root.RowDefinitions.Count < 4)
        {
            if (_zonePoolAttachAttempts < 6)
                Dispatcher.BeginInvoke(new Action(TryAttachZonePoolWorkflow), DispatcherPriority.ApplicationIdle);
            return;
        }

        ZonePoolWorkflowAttached.Add(this, new object());
        root.RowDefinitions.Insert(3, new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement child in root.Children)
        {
            var row = Grid.GetRow(child);
            if (row >= 3) Grid.SetRow(child, row + 1);
        }

        var card = BuildZonePoolWorkflowCard();
        Grid.SetRow(card, 3);
        root.Children.Add(card);
        RefreshZonePoolTargets();
    }

    private Border BuildZonePoolWorkflowCard()
    {
        var card = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var stack = new StackPanel();
        card.Child = stack;

        var header = new DockPanel { LastChildFill = true };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(10, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(24, 86, 57))
        };
        badge.Child = new TextBlock
        {
            Text = "Mob/NPC RUNTIME-VERIFIZIERT",
            Foreground = Brushes.White,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold
        };
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);
        header.Children.Add(new TextBlock
        {
            Text = "Zertifizierter Zone-Pool Workflow",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = (Brush)FindResource("Text")
        });
        stack.Children.Add(header);

        stack.Children.Add(new TextBlock
        {
            Text = "Profil: ShinePlayer 2000 · ShineMob 12000 · ShineNPC 512. " +
                   "Mob 9057/12000 und NPC 293/512 wurden real stabil gemessen; Player >1500 Lasttest ist noch offen. " +
                   "Die GUI verwendet ausschließlich den hashgebundenen Offline-/Testdeployment-Pfad; Live-/In-Place-Patching bleibt gesperrt.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 7)
        });

        var targetRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        targetRow.Children.Add(new TextBlock
        {
            Text = "Testzone",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _zonePoolTargetCombo = new ComboBox { Width = 310, MinHeight = 29, Margin = new Thickness(0, 0, 6, 0) };
        if (TryFindResource("FormComboBox") is Style comboStyle) _zonePoolTargetCombo.Style = comboStyle;
        targetRow.Children.Add(_zonePoolTargetCombo);
        targetRow.Children.Add(CreateZonePoolButton("Zonen neu laden", false, async () =>
        {
            RefreshZonePoolTargets();
            await Task.CompletedTask;
        }));

        _zonePoolWorkflowStatus = new TextBlock
        {
            Text = "Bereit",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 3, 0, 0),
            MaxWidth = 520
        };
        targetRow.Children.Add(_zonePoolWorkflowStatus);
        stack.Children.Add(targetRow);

        var prepareRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        prepareRow.Children.Add(CreateZonePoolButton("1 · Patchkopie erzeugen + prüfen", true,
            () => RunZonePoolUiActionAsync("Patchkopie", CreateAndVerifyZonePoolCopyAsync)));
        prepareRow.Children.Add(CreateZonePoolButton("2 · Preflight", false,
            () => RunZonePoolUiActionAsync("Preflight", RunZonePoolPreflightAsync)));
        prepareRow.Children.Add(CreateZonePoolButton("3 · Testprofil installieren", false,
            () => RunZonePoolUiActionAsync("Testdeployment", DeployZonePoolTestProfileAsync)));
        prepareRow.Children.Add(CreateZonePoolButton("4 · Log-Checkpoint", false,
            () => RunZonePoolUiActionAsync("Log-Checkpoint", SaveZonePoolLogCheckpointAsync)));
        stack.Children.Add(prepareRow);

        var verifyRow = new WrapPanel();
        verifyRow.Children.Add(CreateZonePoolButton("5 · Runtime prüfen", true,
            () => RunZonePoolUiActionAsync("Runtime-Verifikation", VerifyZonePoolRuntimeAsync)));
        verifyRow.Children.Add(CreateZonePoolButton("6 · 5-Minuten-Stabilität", false,
            () => RunZonePoolUiActionAsync("Stabilitätswache", WatchZonePoolStabilityAsync)));
        verifyRow.Children.Add(CreateZonePoolButton("7 · Log-Audit", false,
            () => RunZonePoolUiActionAsync("Log-Audit", AuditZonePoolLogsAsync)));
        verifyRow.Children.Add(CreateZonePoolButton("8 · Rollback auf Stock", false,
            () => RunZonePoolUiActionAsync("Rollback", RollbackZonePoolTestAsync)));
        stack.Children.Add(verifyRow);
        return card;
    }

    private Button CreateZonePoolButton(string text, bool primary, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(2) };
        if (primary && TryFindResource("PrimaryActionButton") is Style primaryStyle) button.Style = primaryStyle;
        button.Click += async (_, _) => await action();
        return button;
    }

    private void RefreshZonePoolTargets()
    {
        if (_zonePoolTargetCombo is null) return;
        var previous = _zonePoolTargetCombo.SelectedItem as string;
        _zonePoolTargetCombo.Items.Clear();

        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(vm.ServerRoot) || !Directory.Exists(vm.ServerRoot))
        {
            SetZonePoolStatus("Server-Root fehlt oder wurde nicht gefunden.");
            return;
        }

        var targets = Directory.EnumerateDirectories(vm.ServerRoot, "Zone*", SearchOption.TopDirectoryOnly)
            .Select(directory => Path.Combine(directory, "Zone.exe"))
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var target in targets) _zonePoolTargetCombo.Items.Add(target);
        if (previous is not null && targets.Contains(previous, StringComparer.OrdinalIgnoreCase))
            _zonePoolTargetCombo.SelectedItem = targets.First(x => x.Equals(previous, StringComparison.OrdinalIgnoreCase));
        else if (targets.Length > 0)
            _zonePoolTargetCombo.SelectedIndex = 0;

        SetZonePoolStatus(targets.Length > 0
            ? $"{targets.Length} Zone-Binary(s) erkannt. Keine Aktion wird automatisch ausgeführt."
            : "Keine Zone.exe im Server-Root gefunden.");
    }

    private async Task RunZonePoolUiActionAsync(string operation, Func<Task<string>> action)
    {
        if (_zonePoolWorkflowBusy)
        {
            SetZonePoolStatus("Eine Zone-Pool-Aktion läuft bereits.");
            return;
        }

        _zonePoolWorkflowBusy = true;
        SetZonePoolStatus(operation + " läuft …");
        try
        {
            SetZonePoolStatus(await action());
        }
        catch (Exception ex)
        {
            SetZonePoolStatus(operation + " FEHLER: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Zone-Pool " + operation, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _zonePoolWorkflowBusy = false;
        }
    }

    private Task<string> CreateAndVerifyZonePoolCopyAsync()
    {
        var target = RequireZonePoolTarget();
        var output = GetZonePoolPatchedCopyPath(target);
        return Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (!File.Exists(output))
            {
                var write = new ZonePoolOfflinePatchWriter().CreatePatchedCopy(target, output, 2000, 12000, 512);
                if (!write.Success) throw new InvalidOperationException(write.Detail);
            }

            var verify = new ZonePoolPatchedCopyVerifier().Verify(output);
            if (!verify.Success) throw new InvalidOperationException(verify.Detail);
            if (verify.VerifiedSiteCount != 83)
                throw new InvalidOperationException($"Patchkopie verifiziert nur {verify.VerifiedSiteCount}/83 Sites.");
            return $"Patchkopie VERIFIED · 83/83 Sites · {ShortHash(verify.PatchedSha256)} · nicht installiert.";
        });
    }

    private Task<string> RunZonePoolPreflightAsync()
    {
        var target = RequireZonePoolTarget();
        var patched = GetZonePoolPatchedCopyPath(target);
        return Task.Run(() =>
        {
            var result = new ZonePoolDeploymentPreflight().Analyze(target, patched);
            if (!result.Ready) throw new InvalidOperationException(result.Detail);
            return "PREFLIGHT READY · Baseline, Patchkopie, Prozessstatus, Backup- und Stagingpfade geprüft.";
        });
    }

    private async Task<string> DeployZonePoolTestProfileAsync()
    {
        var target = RequireZonePoolTarget();
        var patched = GetZonePoolPatchedCopyPath(target);
        var answer = MessageBox.Show(this,
            "Genau eine Test-Zone wird auf das zertifizierte Profil 2000 / 12000 / 512 umgestellt.\n\n" +
            "Voraussetzung: KEINE Zone.exe darf laufen. Baseline-Backup und Deployment-Record sind verpflichtend. " +
            "Es wird kein Prozess automatisch gestartet.\n\nFortfahren?",
            "Zertifiziertes Zone-Pool Testdeployment", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return "Testdeployment vom Benutzer abgebrochen.";

        return await Task.Run(() =>
        {
            var preflight = new ZonePoolDeploymentPreflight().Analyze(target, patched);
            if (!preflight.Ready) throw new InvalidOperationException(preflight.Detail);
            var result = new ZonePoolSingleZoneTestDeployment().Deploy(target, patched, ZonePoolDeployConfirmation);
            if (!result.Success) throw new InvalidOperationException(result.Detail);
            return $"TESTDEPLOYMENT SUCCESS · {ShortHash(result.TargetSha256)} · Zone jetzt normal über den Manager starten.";
        });
    }

    private Task<string> SaveZonePoolLogCheckpointAsync()
    {
        var target = RequireZonePoolTarget();
        var vm = RequireMainViewModel();
        var zoneName = new DirectoryInfo(Path.GetDirectoryName(target)!).Name;
        var directory = GetZonePoolWorkDirectory();
        Directory.CreateDirectory(directory);
        var checkpoint = Path.Combine(directory, $"{zoneName}-log-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        return Task.Run(() =>
        {
            var result = new ZonePoolRuntimeLogDeltaAudit().SaveCheckpoint(vm.ServerRoot, checkpoint);
            if (!result.Success) throw new InvalidOperationException(result.Detail);
            _zonePoolCheckpointPath = checkpoint;
            return $"LOG CHECKPOINT SUCCESS · {result.FileCount} Logs · {Path.GetFileName(checkpoint)}";
        });
    }

    private async Task<string> VerifyZonePoolRuntimeAsync()
    {
        var target = RequireZonePoolTarget();
        var observer = new ZonePoolRuntimeTestObserver();
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var result = await Task.Run(() => observer.Observe(target, 10));
            if (result.Passed && result.Pools is not null)
                return $"RUNTIME PASS · PID {result.ProcessId} · P {result.Pools.PlayerCount}/{result.Pools.PlayerLimit} · " +
                       $"M {result.Pools.MobCount}/{result.Pools.MobLimit} · N {result.Pools.NpcCount}/{result.Pools.NpcLimit}";
            if (result.Blocked) throw new InvalidOperationException(result.Detail);
            await Task.Delay(2000);
        }
        throw new TimeoutException("Keine stabile, verifizierte Test-Zone innerhalb von 120 Sekunden erkannt.");
    }

    private async Task<string> WatchZonePoolStabilityAsync()
    {
        var target = RequireZonePoolTarget();
        var observer = new ZonePoolRuntimeTestObserver();
        ZonePoolRuntimeTestObservation? baseline = null;
        var initialDeadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < initialDeadline)
        {
            var sample = await Task.Run(() => observer.Observe(target, 10));
            if (sample.Blocked) throw new InvalidOperationException(sample.Detail);
            if (sample.Passed && sample.Pools is not null) { baseline = sample; break; }
            await Task.Delay(2000);
        }
        if (baseline?.Pools is null) throw new TimeoutException("Stabilitätswache konnte keinen initialen Runtime-PASS ermitteln.");

        var pid = baseline.ProcessId;
        var started = baseline.ProcessStartedUtc;
        var binaryHash = baseline.Pools.BinarySha256;
        var profile = baseline.Pools.BinaryProfile;
        var playerLimit = baseline.Pools.PlayerLimit;
        var mobLimit = baseline.Pools.MobLimit;
        var npcLimit = baseline.Pools.NpcLimit;
        var maxPlayer = baseline.Pools.PlayerCount;
        var maxMob = baseline.Pools.MobCount;
        var maxNpc = baseline.Pools.NpcCount;
        var samples = 1;
        var deadline = DateTime.UtcNow.AddMinutes(5);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(5000);
            var sample = await Task.Run(() => observer.Observe(target, 10));
            samples++;
            if (!sample.Passed || sample.Pools is null) throw new InvalidOperationException($"Sample {samples} nicht PASS: {sample.Detail}");
            if (sample.ProcessId != pid || sample.ProcessStartedUtc != started) throw new InvalidOperationException("PID/Prozessstart änderte sich; Neustart erkannt.");
            if (!string.Equals(sample.Pools.BinarySha256, binaryHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sample.Pools.BinaryProfile, profile, StringComparison.Ordinal) ||
                sample.Pools.PlayerLimit != playerLimit || sample.Pools.MobLimit != mobLimit || sample.Pools.NpcLimit != npcLimit)
                throw new InvalidOperationException("Binary-Profil oder Runtime-Pool-Maxima änderten sich.");
            maxPlayer = Math.Max(maxPlayer, sample.Pools.PlayerCount);
            maxMob = Math.Max(maxMob, sample.Pools.MobCount);
            maxNpc = Math.Max(maxNpc, sample.Pools.NpcCount);
            SetZonePoolStatus($"5-Min-Watch · {samples} PASS · P {maxPlayer}/{playerLimit} · M {maxMob}/{mobLimit} · N {maxNpc}/{npcLimit}");
        }

        return $"STABILITY PASS · {samples} Samples · unveränderte PID {pid} · Max P {maxPlayer}/{playerLimit} · M {maxMob}/{mobLimit} · N {maxNpc}/{npcLimit}";
    }

    private Task<string> AuditZonePoolLogsAsync()
    {
        var vm = RequireMainViewModel();
        var checkpoint = ResolveZonePoolCheckpoint();
        return Task.Run(() =>
        {
            var result = new ZonePoolRuntimeLogDeltaAudit().Audit(vm.ServerRoot, checkpoint);
            if (!result.Success || result.Blocked) throw new InvalidOperationException(result.Detail);
            var suffix = result.ReviewRequired
                ? $" · REVIEW: {result.Findings.Count} Befund(e), {result.EvidenceGaps.Count} Evidenzlücke(n)"
                : $" · CLEAN · {result.RewrittenFileCount} Rewrite-Datei(en) bytegenau verglichen";
            return result.Detail + suffix;
        });
    }

    private async Task<string> RollbackZonePoolTestAsync()
    {
        var target = RequireZonePoolTarget();
        var answer = MessageBox.Show(this,
            "Die ausgewählte Test-Zone wird auf das verifizierte NA2016-Stock-Backup zurückgesetzt.\n\nKEINE Zone.exe darf laufen. Fortfahren?",
            "Zone-Pool Rollback", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return "Rollback vom Benutzer abgebrochen.";

        return await Task.Run(() =>
        {
            var result = new ZonePoolSingleZoneTestDeployment().Rollback(target, ZonePoolRollbackConfirmation);
            if (!result.Success) throw new InvalidOperationException(result.Detail);
            return $"TESTROLLBACK SUCCESS · Stock {ShortHash(result.TargetSha256)} wiederhergestellt.";
        });
    }

    private string RequireZonePoolTarget()
    {
        if (_zonePoolTargetCombo?.SelectedItem is not string target || !File.Exists(target))
            throw new InvalidOperationException("Bitte zuerst eine gültige Zone.exe auswählen.");
        return target;
    }

    private MainViewModel RequireMainViewModel()
        => DataContext as MainViewModel ?? throw new InvalidOperationException("MainViewModel ist nicht verfügbar.");

    private string GetZonePoolPatchedCopyPath(string target)
    {
        var zoneName = new DirectoryInfo(Path.GetDirectoryName(target)!).Name;
        return Path.Combine(GetZonePoolWorkDirectory(), $"{zoneName}.NextGen-2000-12000-512.exe");
    }

    private static string GetZonePoolWorkDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NextGenFiestaServerManager", "ZonePoolTests");

    private string ResolveZonePoolCheckpoint()
    {
        if (!string.IsNullOrWhiteSpace(_zonePoolCheckpointPath) && File.Exists(_zonePoolCheckpointPath)) return _zonePoolCheckpointPath;
        var target = RequireZonePoolTarget();
        var zoneName = new DirectoryInfo(Path.GetDirectoryName(target)!).Name;
        var directory = GetZonePoolWorkDirectory();
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Noch kein Log-Checkpoint vorhanden.");
        var latest = Directory.EnumerateFiles(directory, $"{zoneName}-log-*.json")
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault();
        if (latest is null) throw new InvalidOperationException("Noch kein Log-Checkpoint für diese Zone vorhanden.");
        _zonePoolCheckpointPath = latest;
        return latest;
    }

    private void SetZonePoolStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetZonePoolStatus(text));
            return;
        }
        if (_zonePoolWorkflowStatus is not null) _zonePoolWorkflowStatus.Text = text;
    }

    private static string ShortHash(string value)
        => string.IsNullOrWhiteSpace(value) ? "kein SHA" : value[..Math.Min(12, value.Length)];
}
