using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NextGen.Fiesta.ServerManager.Services;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private ComboBox? _zoneLoadCaptureInterfaceCombo;
    private FiestaPacketCaptureSession? _zoneLoadCaptureSession;
    private bool _zoneLoadCaptureCloseHooked;

    private FrameworkElement BuildZoneLoadCaptureRecorderPanel()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };

        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = "Capture",
            Width = 112,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedStrong")
        });

        _zoneLoadCaptureInterfaceCombo = new ComboBox
        {
            Width = 350,
            MinHeight = 29,
            Margin = new Thickness(0, 0, 6, 0),
            DisplayMemberPath = nameof(FiestaCaptureInterface.DisplayName),
            ToolTip = "Für einen lokalen Server normalerweise 'Adapter for loopback traffic capture' auswählen."
        };
        if (TryFindResource("FormComboBox") is Style comboStyle)
            _zoneLoadCaptureInterfaceCombo.Style = comboStyle;
        row.Children.Add(_zoneLoadCaptureInterfaceCombo);

        row.Children.Add(CreateZonePoolButton("Interfaces", false, RefreshZoneLoadCaptureInterfacesAsync));
        row.Children.Add(CreateZonePoolButton("Capture starten", true, StartZoneLoadCaptureAsync));
        row.Children.Add(CreateZonePoolButton("Stop + importieren", false, StopAndImportZoneLoadCaptureAsync));
        stack.Children.Add(row);

        stack.Children.Add(new TextBlock
        {
            Text = "Capture bleibt ausschließlich lokal. Der rohe PCAP enthält entschlüsselbaren Login-Verkehr: nur einen Wegwerf-Testaccount verwenden und die PCAP-Datei nach erfolgreichem Template-Import löschen. " +
                   "Aufnahmefilter: Fiesta TCP 9000–9100 (plus abweichender Login-/Zone-Port). Maximal 180 Sekunden.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap
        });

        EnsureZoneLoadCaptureCloseHook();
        return stack;
    }

    private async Task RefreshZoneLoadCaptureInterfacesAsync()
    {
        if (_zoneLoadCaptureInterfaceCombo is null)
            return;

        SetZoneLoadStatus("Suche Wireshark/dumpcap Capture-Interfaces …");
        try
        {
            var interfaces = await Task.Run(() => new FiestaPacketCaptureRecorder().ListInterfaces());
            _zoneLoadCaptureInterfaceCombo.Items.Clear();
            foreach (var item in interfaces)
                _zoneLoadCaptureInterfaceCombo.Items.Add(item);

            if (interfaces.Count == 0)
            {
                SetZoneLoadStatus("Keine dumpcap Capture-Interfaces gefunden.");
                return;
            }

            var preferred = interfaces
                .Select((item, index) => new { item, index })
                .FirstOrDefault(x =>
                    x.item.DisplayName.Contains("loopback", StringComparison.OrdinalIgnoreCase)
                    || x.item.DisplayName.Contains("Npcap Loopback", StringComparison.OrdinalIgnoreCase));
            _zoneLoadCaptureInterfaceCombo.SelectedIndex = preferred?.index ?? 0;
            SetZoneLoadStatus(
                $"{interfaces.Count} Capture-Interface(s) gefunden · ausgewählt: {(_zoneLoadCaptureInterfaceCombo.SelectedItem as FiestaCaptureInterface)?.DisplayName}");
        }
        catch (Exception ex)
        {
            SetZoneLoadStatus("Capture-Interfaces FEHLER: " + ex.Message);
            MessageBox.Show(this,
                ex.Message + "\n\nWireshark inklusive Npcap installieren. Für einen lokalen Server anschließend den Loopback-Adapter auswählen.",
                "Player Load Capture",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task StartZoneLoadCaptureAsync()
    {
        if (_zoneLoadCaptureSession is { IsRunning: true })
        {
            SetZoneLoadStatus($"Capture läuft bereits · PID {_zoneLoadCaptureSession.ProcessId} · {_zoneLoadCaptureSession.OutputPath}");
            return;
        }

        if (_zoneLoadCaptureInterfaceCombo?.SelectedItem is not FiestaCaptureInterface captureInterface)
        {
            await RefreshZoneLoadCaptureInterfacesAsync();
            if (_zoneLoadCaptureInterfaceCombo?.SelectedItem is not FiestaCaptureInterface refreshed)
                throw new InvalidOperationException("Kein Capture-Interface ausgewählt.");
            captureInterface = refreshed;
        }

        var target = RequireZonePoolTarget();
        var loginPort = ParseZoneLoadInt(_zoneLoadLoginPortBox, "Login-Port", 1, 65535);
        var zonePort = ResolveSelectedZoneClientPort(target);
        var directory = GetZonePoolWorkDirectory();
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"na2016-client-{DateTime.Now:yyyyMMdd-HHmmss}.pcapng");

        var session = await Task.Run(() => new FiestaPacketCaptureRecorder().Start(
            new FiestaPacketCaptureOptions
            {
                InterfaceSelector = captureInterface.Selector,
                OutputPath = output,
                LoginPort = loginPort,
                ZonePort = zonePort,
                MaxDurationSeconds = 180
            }));

        _zoneLoadCaptureSession?.Dispose();
        _zoneLoadCaptureSession = session;
        if (_zoneLoadCapturePathBox is not null)
            _zoneLoadCapturePathBox.Text = output;

        SetZoneLoadStatus(
            $"CAPTURE LÄUFT · PID {session.ProcessId} · {captureInterface.DisplayName} · Filter {session.Filter}. " +
            "Jetzt EINEN echten Testclient vollständig Login → World → Charakter → Zone durchführen; danach 'Stop + importieren'.");
    }

    private async Task StopAndImportZoneLoadCaptureAsync()
    {
        if (_zoneLoadCaptureSession is null)
            throw new InvalidOperationException("Keine vom Manager gestartete Capture-Session vorhanden.");

        var session = _zoneLoadCaptureSession;
        _zoneLoadCaptureSession = null;
        var stopped = await Task.Run(session.Stop);
        session.Dispose();

        if (_zoneLoadCapturePathBox is not null)
            _zoneLoadCapturePathBox.Text = stopped.CapturePath;

        if (!stopped.Success)
            throw new InvalidOperationException(stopped.Detail);

        SetZoneLoadStatus(stopped.Detail + " · validiere/importiere jetzt Login, World, CharacterCreate und Zone …");
        await ImportZoneTransferCaptureAsync();
    }

    private void EnsureZoneLoadCaptureCloseHook()
    {
        if (_zoneLoadCaptureCloseHooked)
            return;

        _zoneLoadCaptureCloseHooked = true;
        Closed += (_, _) =>
        {
            var session = _zoneLoadCaptureSession;
            _zoneLoadCaptureSession = null;
            if (session is null) return;
            try { session.Stop(); } catch { }
            try { session.Dispose(); } catch { }
        };
    }
}
