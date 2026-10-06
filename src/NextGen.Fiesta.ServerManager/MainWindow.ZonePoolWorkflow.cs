using System.Runtime.CompilerServices;
using Microsoft.Win32;
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

    private TextBox? _zoneLoadCapturePathBox;
    private TextBox? _zoneLoadCaptureCharacterBox;
    private TextBox? _zoneLoadTemplatePathBox;
    private TextBox? _zoneLoadCharacterCreateTemplatePathBox;
    private TextBox? _zoneLoadCredentialPathBox;
    private TextBox? _zoneLoadIdentityCountBox;
    private TextBox? _zoneLoadIdentityPrefixBox;
    private TextBox? _zoneLoadLoginHostBox;
    private TextBox? _zoneLoadLoginPortBox;
    private TextBox? _zoneLoadWorldIdBox;
    private TextBox? _zoneLoadClientYearBox;
    private TextBox? _zoneLoadClientVersionBox;
    private TextBox? _zoneLoadFileHashBox;
    private TextBlock? _zoneLoadStatus;
    private CancellationTokenSource? _zoneLoadRampCancellation;

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
        var mainNavigation = _mainNavigation;
        if (mainNavigation is null ||
            mainNavigation.Items.Count < 2 ||
            mainNavigation.Items[1] is not TabItem serverTab ||
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
                   "Für den Player-Test wird ausschließlich die ausgewählte zertifizierte Zone transaktional auf 2000 Client-Sessions gesetzt; Stock-Zonen bleiben bei 1500. " +
                   "Live-/In-Place-Patching bleibt gesperrt.",
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
        prepareRow.Children.Add(CreateZonePoolButton("4 · Listener 2000 aktivieren", false,
            () => RunZonePoolUiActionAsync("Listener 2000", EnableZonePoolListenerAsync)));
        prepareRow.Children.Add(CreateZonePoolButton("5 · Log-Checkpoint", false,
            () => RunZonePoolUiActionAsync("Log-Checkpoint", SaveZonePoolLogCheckpointAsync)));
        stack.Children.Add(prepareRow);

        var verifyRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        verifyRow.Children.Add(CreateZonePoolButton("6 · Runtime prüfen", true,
            () => RunZonePoolUiActionAsync("Runtime-Verifikation", VerifyZonePoolRuntimeAsync)));
        verifyRow.Children.Add(CreateZonePoolButton("7 · 5-Minuten-Stabilität", false,
            () => RunZonePoolUiActionAsync("Stabilitätswache", WatchZonePoolStabilityAsync)));
        verifyRow.Children.Add(CreateZonePoolButton("8 · Log-Audit", false,
            () => RunZonePoolUiActionAsync("Log-Audit", AuditZonePoolLogsAsync)));
        stack.Children.Add(verifyRow);

        var rollbackRow = new WrapPanel();
        rollbackRow.Children.Add(CreateZonePoolButton("9 · Komplett-Rollback auf Stock", false,
            () => RunZonePoolUiActionAsync("Komplett-Rollback", RollbackZonePoolTestAsync)));
        rollbackRow.Children.Add(new TextBlock
        {
            Text = "Rollback-Reihenfolge ist fest: zuerst Listener/ServerInfo 1500, danach Zone.exe Stock.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        });
        stack.Children.Add(rollbackRow);

        stack.Children.Add(BuildPlayerLoadVerificationExpander());
        return card;
    }

    private Expander BuildPlayerLoadVerificationExpander()
    {
        var expander = new Expander
        {
            Header = "Player Load Verification · echter Login → World → Zone → ShinePlayer",
            IsExpanded = false,
            Margin = new Thickness(0, 9, 0, 0),
            Foreground = (Brush)FindResource("Text")
        };

        var stack = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = "Ziel: den noch offenen ShinePlayer-Nachweis oberhalb 1500 ohne grafische Clients durchführen. " +
                   "Ein Test zählt nur, wenn der Headless-Client ClientReady erreicht UND der echte ShinePlayer-Zähler der laufenden zertifizierten Zone exakt mitsteigt. " +
                   "Der Test startet/stopppt keine Serverdienste und verändert keine Binary.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 7)
        });

        var captureRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        captureRow.Children.Add(new TextBlock
        {
            Text = "Real-Client Capture",
            Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedStrong")
        });
        _zoneLoadCapturePathBox = CreateZoneLoadTextBox(330, "Wireshark .pcap/.pcapng");
        captureRow.Children.Add(_zoneLoadCapturePathBox);
        captureRow.Children.Add(CreateZonePoolButton("PCAP wählen", false, async () =>
        {
            BrowseZoneLoadCapture();
            await Task.CompletedTask;
        }));
        _zoneLoadCaptureCharacterBox = CreateZoneLoadTextBox(125, "Charaktername");
        captureRow.Children.Add(_zoneLoadCaptureCharacterBox);
        captureRow.Children.Add(CreateZonePoolButton("Capture importieren", true,
            () => RunZonePoolUiActionAsync("Zone-Transfer Import", ImportZoneTransferCaptureAsync)));
        stack.Children.Add(captureRow);

        var filesRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        filesRow.Children.Add(new TextBlock
        {
            Text = "Template",
            Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedStrong")
        });
        _zoneLoadTemplatePathBox = CreateZoneLoadTextBox(310, "CH6/1 Template JSON");
        filesRow.Children.Add(_zoneLoadTemplatePathBox);
        filesRow.Children.Add(CreateZonePoolButton("Template…", false, async () =>
        {
            BrowseZoneLoadTemplate();
            await Task.CompletedTask;
        }));
        filesRow.Children.Add(new TextBlock
        {
            Text = "Credentials",
            Width = 70,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedStrong")
        });
        _zoneLoadCredentialPathBox = CreateZoneLoadTextBox(300, "Load-Credentials JSON");
        filesRow.Children.Add(_zoneLoadCredentialPathBox);
        filesRow.Children.Add(CreateZonePoolButton("Credentials…", false, async () =>
        {
            BrowseZoneLoadCredentials();
            await Task.CompletedTask;
        }));
        stack.Children.Add(filesRow);

        var identityRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        identityRow.Children.Add(new TextBlock
        {
            Text = "Auto-Create",
            Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedStrong")
        });
        _zoneLoadCharacterCreateTemplatePathBox = CreateZoneLoadTextBox(310, "capture-basiertes CH5/1 CharacterCreate Template");
        identityRow.Children.Add(_zoneLoadCharacterCreateTemplatePathBox);
        identityRow.Children.Add(CreateZonePoolButton("CH5/1 Template…", false, async () =>
        {
            BrowseZoneLoadCharacterCreateTemplate();
            await Task.CompletedTask;
        }));
        identityRow.Children.Add(new TextBlock
        {
            Text = "Anzahl",
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 3, 0)
        });
        _zoneLoadIdentityCountBox = CreateZoneLoadTextBox(58, "1..2000", "1600");
        identityRow.Children.Add(_zoneLoadIdentityCountBox);
        identityRow.Children.Add(new TextBlock
        {
            Text = "Prefix",
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 3, 0)
        });
        _zoneLoadIdentityPrefixBox = CreateZoneLoadTextBox(70, "Account-/Char-Prefix", "ngl");
        identityRow.Children.Add(_zoneLoadIdentityPrefixBox);
        identityRow.Children.Add(CreateZonePoolButton("SQL + Credentials erzeugen", false,
            () => RunZonePoolUiActionAsync("Load-Identitäten", GenerateLoadIdentitiesAsync)));
        stack.Children.Add(identityRow);

        var networkRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        networkRow.Children.Add(new TextBlock
        {
            Text = "Login-Protokoll",
            Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedStrong")
        });
        networkRow.Children.Add(new TextBlock { Text = "Host", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
        _zoneLoadLoginHostBox = CreateZoneLoadTextBox(112, "127.0.0.1", "127.0.0.1");
        networkRow.Children.Add(_zoneLoadLoginHostBox);
        networkRow.Children.Add(new TextBlock { Text = "Port", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 3, 0) });
        _zoneLoadLoginPortBox = CreateZoneLoadTextBox(58, "9010", "9010");
        networkRow.Children.Add(_zoneLoadLoginPortBox);
        networkRow.Children.Add(new TextBlock { Text = "World", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 3, 0) });
        _zoneLoadWorldIdBox = CreateZoneLoadTextBox(42, "0", "0");
        networkRow.Children.Add(_zoneLoadWorldIdBox);
        networkRow.Children.Add(new TextBlock { Text = "Year", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 3, 0) });
        _zoneLoadClientYearBox = CreateZoneLoadTextBox(54, "2016", "2016");
        networkRow.Children.Add(_zoneLoadClientYearBox);
        networkRow.Children.Add(new TextBlock { Text = "Version", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 3, 0) });
        _zoneLoadClientVersionBox = CreateZoneLoadTextBox(48, "2", "2");
        networkRow.Children.Add(_zoneLoadClientVersionBox);
        networkRow.Children.Add(new TextBlock { Text = "FileHash", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 3, 0) });
        _zoneLoadFileHashBox = CreateZoneLoadTextBox(155, "optional");
        networkRow.Children.Add(_zoneLoadFileHashBox);
        stack.Children.Add(networkRow);

        var actionRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        actionRow.Children.Add(CreateZonePoolButton("A · 1 Client + ShinePlayer beweisen", true,
            () => RunPlayerLoadRampUiAsync(singleClientOnly: true)));
        actionRow.Children.Add(CreateZonePoolButton("B · Ramp 1 → 1600", false,
            () => RunPlayerLoadRampUiAsync(singleClientOnly: false)));
        actionRow.Children.Add(CreateZonePoolButton("Abbrechen", false, async () =>
        {
            _zoneLoadRampCancellation?.Cancel();
            SetZoneLoadStatus("Abbruch angefordert …");
            await Task.CompletedTask;
        }));
        stack.Children.Add(actionRow);

        _zoneLoadStatus = new TextBlock
        {
            Text = "Noch kein Player-Loadtest ausgeführt. Capture importieren; für Auto-Create muss der Mitschnitt zusätzlich eine echte Charaktererstellung enthalten.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(_zoneLoadStatus);

        expander.Content = stack;
        return expander;
    }

    private TextBox CreateZoneLoadTextBox(double width, string toolTip, string? initialText = null)
    {
        var box = new TextBox
        {
            Width = width,
            MinHeight = 28,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 5, 0),
            ToolTip = toolTip,
            Text = initialText ?? string.Empty
        };
        return box;
    }

    private void BrowseZoneLoadCapture()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Wireshark-Mitschnitt mit einem vollständigen Fiesta-Login auswählen",
            Filter = "Packet Capture (*.pcap;*.pcapng)|*.pcap;*.pcapng|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && _zoneLoadCapturePathBox is not null)
            _zoneLoadCapturePathBox.Text = dialog.FileName;
    }

    private void BrowseZoneLoadTemplate()
    {
        var dialog = new OpenFileDialog
        {
            Title = "NA2016 Zone-Transfer Template auswählen",
            Filter = "JSON (*.json)|*.json|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && _zoneLoadTemplatePathBox is not null)
            _zoneLoadTemplatePathBox.Text = dialog.FileName;
    }

    private void BrowseZoneLoadCharacterCreateTemplate()
    {
        var dialog = new OpenFileDialog
        {
            Title = "NA2016 CharacterCreate Template auswählen",
            Filter = "JSON (*.json)|*.json|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && _zoneLoadCharacterCreateTemplatePathBox is not null)
            _zoneLoadCharacterCreateTemplatePathBox.Text = dialog.FileName;
    }

    private void BrowseZoneLoadCredentials()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Headless-Load-Credentials auswählen",
            Filter = "JSON (*.json)|*.json|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && _zoneLoadCredentialPathBox is not null)
            _zoneLoadCredentialPathBox.Text = dialog.FileName;
    }

    private async Task<string> ImportZoneTransferCaptureAsync()
    {
        var target = RequireZonePoolTarget();
        var capture = _zoneLoadCapturePathBox?.Text.Trim();
        var character = _zoneLoadCaptureCharacterBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(capture) || !File.Exists(capture))
            throw new InvalidOperationException("Bitte zuerst eine vorhandene .pcap/.pcapng-Datei auswählen.");
        if (string.IsNullOrWhiteSpace(character))
            throw new InvalidOperationException("Bitte den Charaktername des im Mitschnitt eingeloggten Clients angeben.");

        var zoneName = new DirectoryInfo(Path.GetDirectoryName(target)!).Name;
        var output = Path.Combine(GetZonePoolWorkDirectory(), $"{zoneName}-zone-transfer-template.json");
        var zonePort = ResolveSelectedZoneClientPort(target);

        var loginPort = ParseZoneLoadInt(_zoneLoadLoginPortBox, "Login-Port", 1, 65535);
        var profileOutput = Path.Combine(GetZonePoolWorkDirectory(), $"{zoneName}-client-capture-profile.json");

        var characterCreateOutput = Path.Combine(
            GetZonePoolWorkDirectory(),
            $"{zoneName}-character-create-template.json");

        var imported = await Task.Run(() =>
        {
            var zoneResult = new FiestaZoneTransferCaptureImporter().Import(
                new FiestaZoneTransferCaptureOptions
                {
                    CapturePath = capture,
                    OutputTemplatePath = output,
                    ZonePort = zonePort,
                    ExpectedCharacterName = character
                });
            if (!zoneResult.Success)
                return (
                    Zone: zoneResult,
                    Client: (FiestaClientCaptureProfileResult?)null,
                    Character: (FiestaCharacterCreateCaptureResult?)null);

            var clientResult = new FiestaClientCaptureProfileImporter().Import(
                new FiestaClientCaptureProfileOptions
                {
                    CapturePath = capture,
                    OutputProfilePath = profileOutput,
                    LoginPort = loginPort
                });
            if (!clientResult.Success || clientResult.Profile is null)
                return (Zone: zoneResult, Client: clientResult, Character: (FiestaCharacterCreateCaptureResult?)null);

            var characterResult = new FiestaCharacterCreateCaptureImporter().Import(
                new FiestaCharacterCreateCaptureOptions
                {
                    CapturePath = capture,
                    OutputTemplatePath = characterCreateOutput,
                    WorldPort = clientResult.Profile.WorldPort,
                    ExpectedCharacterName = character
                });

            return (Zone: zoneResult, Client: clientResult, Character: characterResult);
        });

        if (!imported.Zone.Success)
            throw new InvalidOperationException(imported.Zone.Detail);
        if (imported.Client is null || !imported.Client.Success || imported.Client.Profile is null)
        {
            throw new InvalidOperationException(
                imported.Zone.Detail + Environment.NewLine +
                (imported.Client?.Detail ?? "CLIENT CAPTURE PROFILE: BLOCKED · Profilimport wurde nicht ausgeführt."));
        }

        var profile = imported.Client.Profile;
        if (_zoneLoadTemplatePathBox is not null) _zoneLoadTemplatePathBox.Text = imported.Zone.TemplatePath;
        if (imported.Character?.Success == true && _zoneLoadCharacterCreateTemplatePathBox is not null)
            _zoneLoadCharacterCreateTemplatePathBox.Text = imported.Character.TemplatePath;
        if (_zoneLoadLoginHostBox is not null) _zoneLoadLoginHostBox.Text = profile.LoginHost;
        if (_zoneLoadLoginPortBox is not null) _zoneLoadLoginPortBox.Text = profile.LoginPort.ToString();
        if (_zoneLoadWorldIdBox is not null) _zoneLoadWorldIdBox.Text = profile.WorldId.ToString();
        if (_zoneLoadClientYearBox is not null) _zoneLoadClientYearBox.Text = profile.ClientYear.ToString();
        if (_zoneLoadClientVersionBox is not null) _zoneLoadClientVersionBox.Text = profile.ClientVersion.ToString();
        if (_zoneLoadFileHashBox is not null) _zoneLoadFileHashBox.Text = profile.FileHash ?? string.Empty;

        var createDetail = imported.Character?.Success == true
            ? imported.Character.Detail
            : "CH5/1 nicht im Capture: 1-Client-Test mit vorhandenem Charakter möglich; Auto-Create-Ramp benötigt einen Capture mit echter Charaktererstellung.";

        var detail = imported.Zone.Detail + " · " + imported.Client.Detail + " · " +
                     createDetail + $" · PCAP {ShortHash(profile.SourceCaptureSha256)}";
        SetZoneLoadStatus(detail);
        return detail;
    }

    private async Task<string> GenerateLoadIdentitiesAsync()
    {
        var count = ParseZoneLoadInt(_zoneLoadIdentityCountBox, "Account-Anzahl", 1, 2000);
        var prefix = _zoneLoadIdentityPrefixBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(prefix))
            throw new InvalidOperationException("Account-/Char-Prefix fehlt.");

        var result = await Task.Run(() => new FiestaLoadIdentityGenerator().Generate(
            new FiestaLoadIdentityGenerationOptions
            {
                Count = count,
                UsernamePrefix = prefix.ToLowerInvariant(),
                CharacterPrefix = prefix.ToUpperInvariant(),
                CharacterSlot = 0,
                AccountDatabase = "Account",
                OutputDirectory = GetZonePoolWorkDirectory()
            }));

        if (_zoneLoadCredentialPathBox is not null)
            _zoneLoadCredentialPathBox.Text = result.CredentialManifestPath;

        var detail = result.Detail +
                     $" · SQL SHA {ShortHash(result.SqlSha256)} · Manifest SHA {ShortHash(result.ManifestSha256)} · " +
                     "WICHTIG: Das SQL-Skript vor dem Rampentest einmal bewusst gegen die lokale TEST-Account-Datenbank ausführen; der Manager führt es nicht automatisch aus.";
        SetZoneLoadStatus(detail);
        return detail;
    }

    private async Task RunPlayerLoadRampUiAsync(bool singleClientOnly)
    {
        if (_zonePoolWorkflowBusy)
        {
            SetZoneLoadStatus("Eine Zone-Pool-/Load-Aktion läuft bereits.");
            return;
        }

        _zonePoolWorkflowBusy = true;
        _zoneLoadRampCancellation?.Dispose();
        _zoneLoadRampCancellation = new CancellationTokenSource();
        var cancellation = _zoneLoadRampCancellation;

        try
        {
            var target = RequireZonePoolTarget();
            var template = _zoneLoadTemplatePathBox?.Text.Trim();
            var characterCreateTemplate = _zoneLoadCharacterCreateTemplatePathBox?.Text.Trim();
            var credentials = _zoneLoadCredentialPathBox?.Text.Trim();
            if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
                throw new InvalidOperationException("Gültiges CH6/1-Template fehlt. Erst Capture importieren oder Template auswählen.");
            if (string.IsNullOrWhiteSpace(credentials) || !File.Exists(credentials))
                throw new InvalidOperationException("Credential-Manifest fehlt.");

            var loginHost = string.IsNullOrWhiteSpace(_zoneLoadLoginHostBox?.Text)
                ? "127.0.0.1"
                : _zoneLoadLoginHostBox.Text.Trim();
            var loginPort = ParseZoneLoadInt(_zoneLoadLoginPortBox, "Login-Port", 1, 65535);
            var worldId = ParseZoneLoadInt(_zoneLoadWorldIdBox, "World-ID", 0, 255);
            var clientYear = ParseZoneLoadInt(_zoneLoadClientYearBox, "Client-Year", 1, ushort.MaxValue);
            var clientVersion = ParseZoneLoadInt(_zoneLoadClientVersionBox, "Client-Version", 1, ushort.MaxValue);
            var fileHash = _zoneLoadFileHashBox?.Text.Trim();
            if (string.IsNullOrWhiteSpace(fileHash) || fileHash.Equals("optional", StringComparison.OrdinalIgnoreCase))
                fileHash = null;

            var options = new FiestaLoadRampOptions
            {
                TargetZoneExePath = target,
                CredentialManifestPath = credentials,
                ClientOptions = new FiestaHeadlessProbeOptions
                {
                    LoginHost = loginHost,
                    LoginPort = loginPort,
                    WorldId = checked((byte)worldId),
                    ClientYear = checked((ushort)clientYear),
                    ClientVersion = checked((ushort)clientVersion),
                    FileHash = fileHash,
                    ZoneTransferTemplatePath = template,
                    CharacterCreateTemplatePath =
                        !string.IsNullOrWhiteSpace(characterCreateTemplate) && File.Exists(characterCreateTemplate)
                            ? characterCreateTemplate
                            : null,
                    StepTimeout = TimeSpan.FromSeconds(15),
                    ZoneLoginTimeout = TimeSpan.FromSeconds(30),
                    HoldDuration = singleClientOnly ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1)
                },
                StageTargets = singleClientOnly
                    ? new[] { 1 }
                    : new[] { 1, 10, 100, 500, 1000, 1450, 1510, 1600 },
                ClientStartInterval = singleClientOnly ? TimeSpan.Zero : TimeSpan.FromMilliseconds(50),
                StageSettleTime = singleClientOnly ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(10),
                SessionHoldDuration = singleClientOnly ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1),
                FinalStabilityDuration = singleClientOnly ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(5),
                StabilityPollInterval = TimeSpan.FromSeconds(5)
            };

            SetZoneLoadStatus(singleClientOnly
                ? "1-Client-Probe läuft: Login → World → Zone → ShinePlayer …"
                : "Load-Ramp läuft: 1 → 10 → 100 → 500 → 1000 → 1450 → 1510 → 1600 …");

            var result = await new FiestaLoadRampCoordinator().RunAsync(
                options,
                p =>
                {
                    if (p.Phase is "STAGE-PASS" or "STAGE-FAIL" or "STABILITY" or "CLIENT-FAIL" ||
                        (p.Phase == "HOLDING" && (p.ReadyClients <= 10 || p.ReadyClients % 25 == 0)) ||
                        p.Phase == "WAIT-READY")
                    {
                        SetZoneLoadStatus(
                            $"{p.Phase} · Ziel {p.TargetClients:N0} · Ready {p.ReadyClients:N0} · ShinePlayer {p.ServerPlayers:N0} · {p.Detail}");
                    }
                },
                cancellation.Token);

            SetZoneLoadStatus(result.Detail);
            if (result.Blocked)
            {
                MessageBox.Show(this, result.Detail, "Player Load Verification", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (result.Passed)
            {
                MessageBox.Show(this, result.Detail, "Player Load Verification", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            SetZoneLoadStatus("Player-Loadtest abgebrochen.");
        }
        catch (Exception ex)
        {
            SetZoneLoadStatus("Player-Loadtest FEHLER: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Player Load Verification", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _zonePoolWorkflowBusy = false;
            cancellation.Dispose();
            if (ReferenceEquals(_zoneLoadRampCancellation, cancellation))
                _zoneLoadRampCancellation = null;
        }
    }

    private int ResolveSelectedZoneClientPort(string targetZoneExePath)
    {
        if (DataContext is MainViewModel vm)
        {
            var match = vm.Services.FirstOrDefault(service =>
                !string.IsNullOrWhiteSpace(service.ExecutablePath) &&
                PathEqualsSafe(service.ExecutablePath, targetZoneExePath));
            if (match?.ClientPort is int port && port is > 0 and <= 65535)
                return port;
        }
        return 9016;
    }

    private static int ParseZoneLoadInt(TextBox? box, string label, int min, int max)
    {
        if (box is null || !int.TryParse(box.Text.Trim(), out var value) || value < min || value > max)
            throw new InvalidOperationException($"{label} muss zwischen {min} und {max} liegen.");
        return value;
    }

    private static bool PathEqualsSafe(string left, string right)
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

    private void SetZoneLoadStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetZoneLoadStatus(text)));
            return;
        }

        if (_zoneLoadStatus is not null)
            _zoneLoadStatus.Text = text;
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
            return $"TESTDEPLOYMENT SUCCESS · {ShortHash(result.TargetSha256)} · noch NICHT starten: zuerst Schritt 4 Listener 2000 und Schritt 5 Log-Checkpoint.";
        });
    }

    private async Task<string> EnableZonePoolListenerAsync()
    {
        var target = RequireZonePoolTarget();
        var service = new ZoneClientListenerTestConfiguration();
        var readiness = await Task.Run(() => service.AnalyzeApplyReadiness(target));
        if (!readiness.Ready) throw new InvalidOperationException(readiness.Detail);

        var entry = readiness.TargetEntry;
        var answer = MessageBox.Show(this,
            $"Nur die ausgewählte zertifizierte Test-Zone wird in ServerInfo.txt von 1500 auf 2000 Client-Sessions gesetzt.\n\n" +
            $"Ziel: {entry?.Name ?? Path.GetFileName(Path.GetDirectoryName(target))} · World {entry?.WorldNo} · Zone {entry?.ZoneNo}\n" +
            "Alle anderen Zone-Listener bleiben unverändert. Backup und Manifest sind verpflichtend; KEINE Zone.exe darf laufen. " +
            "Es wird kein Prozess automatisch gestartet.\n\nFortfahren?",
            "Zertifizierten Zone-Listener 2000 aktivieren", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return "Listener-Aktivierung vom Benutzer abgebrochen.";

        return await Task.Run(() =>
        {
            var result = new ZoneClientListenerTestConfiguration().Apply(
                target,
                ZoneClientListenerTestConfiguration.ApplyConfirmationToken);
            if (!result.Success || !result.Applied) throw new InvalidOperationException(result.Detail);
            return $"LISTENER 2000 APPLIED · nur Zone {result.Readiness?.ZoneNo} · ServerInfo Backup/Manifest verifiziert · jetzt Schritt 5 Log-Checkpoint, danach Test-Zone manuell starten.";
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
            return $"LOG CHECKPOINT SUCCESS · {result.FileCount} Logs · {Path.GetFileName(checkpoint)} · Test-Zone kann jetzt manuell gestartet werden.";
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
            "Der komplette Ein-Zonen-Test wird sicher zurückgesetzt.\n\n" +
            "Reihenfolge: (1) Listener/ServerInfo zurück auf 1500, (2) Zone.exe zurück auf die verifizierte NA2016-Baseline. " +
            "Wenn der Listener bereits Stock ist, wird das vor dem Binary-Rollback erneut geprüft.\n\nKEINE Zone.exe darf laufen. Fortfahren?",
            "Zone-Pool Komplett-Rollback", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return "Komplett-Rollback vom Benutzer abgebrochen.";

        return await Task.Run(() =>
        {
            var listener = new ZoneClientListenerTestConfiguration().Rollback(
                target,
                ZoneClientListenerTestConfiguration.RollbackConfirmationToken);

            string listenerStatus;
            if (listener.Success)
            {
                listenerStatus = "Listener 1500 wiederhergestellt";
            }
            else
            {
                if (!IsZoneListenerStock(target, out var stockDetail))
                    throw new InvalidOperationException("Listener-Rollback konnte nicht sicher abgeschlossen werden: " + listener.Detail + " " + stockDetail);
                listenerStatus = "Listener bereits verifiziert auf Stock 1500";
            }

            var result = new ZonePoolSingleZoneTestDeployment().Rollback(target, ZonePoolRollbackConfirmation);
            if (!result.Success) throw new InvalidOperationException(result.Detail);
            return $"KOMPLETT-ROLLBACK SUCCESS · {listenerStatus} · Zone.exe Stock {ShortHash(result.TargetSha256)} wiederhergestellt.";
        });
    }

    private static bool IsZoneListenerStock(string targetZoneExePath, out string detail)
    {
        detail = string.Empty;
        try
        {
            var zoneDirectory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(targetZoneExePath))!);
            if (!zoneDirectory.Name.StartsWith("Zone", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(zoneDirectory.Name.AsSpan(4), out var zoneNo))
            {
                detail = "Zielordner kann nicht eindeutig als ZoneNN aufgelöst werden.";
                return false;
            }

            var serverRoot = zoneDirectory.Parent;
            if (serverRoot is null)
            {
                detail = "Server-Root kann nicht aus dem Zielordner abgeleitet werden.";
                return false;
            }

            var serverInfoPath = Path.Combine(serverRoot.FullName, "9Data", "ServerInfo", "ServerInfo.txt");
            if (!File.Exists(serverInfoPath))
            {
                detail = "ServerInfo.txt fehlt; Stockzustand kann nicht bewiesen werden.";
                return false;
            }

            var candidates = new ServerInfoParser().Parse(serverInfoPath)
                .Where(x => x.ServerType == 6 && x.ZoneNo == zoneNo && x.ConnectionKind == 20)
                .ToList();
            if (candidates.Count != 1)
            {
                detail = $"Client-Listener der Zone {zoneNo} ist nicht eindeutig (Treffer: {candidates.Count}).";
                return false;
            }

            if (candidates[0].MaxAccept != ZoneClientListenerTestConfiguration.StockMaxAccept)
            {
                detail = $"Client-Listener steht noch auf {candidates[0].MaxAccept:N0} statt Stock {ZoneClientListenerTestConfiguration.StockMaxAccept:N0}.";
                return false;
            }

            detail = $"Zone {zoneNo} Client-Listener steht verifiziert auf Stock {ZoneClientListenerTestConfiguration.StockMaxAccept:N0}.";
            return true;
        }
        catch (Exception ex)
        {
            detail = "Stockprüfung des Zone-Listeners fehlgeschlagen: " + ex.Message;
            return false;
        }
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
