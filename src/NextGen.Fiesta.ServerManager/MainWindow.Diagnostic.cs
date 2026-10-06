using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using NextGen.Fiesta.ServerManager.ViewModels;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<MainWindow, object> TargetDiagnostics = new();
    private static readonly bool DiagnosticLoadedHookRegistered = RegisterDiagnosticLoadedHook();

    private static bool RegisterDiagnosticLoadedHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnDiagnosticWindowLoaded));
        return true;
    }

    private static void OnDiagnosticWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || TargetDiagnostics.TryGetValue(window, out _))
            return;

        var mainNavigation = window._mainNavigation;
        if (mainNavigation is null || mainNavigation.Items.Count < 3 ||
            mainNavigation.Items[2] is not TabItem diagnosticTab ||
            diagnosticTab.Content is not TabControl diagnosticNavigation ||
            diagnosticNavigation.Items.Count < 3 ||
            diagnosticNavigation.Items[0] is not TabItem logs ||
            diagnosticNavigation.Items[1] is not TabItem timeline ||
            diagnosticNavigation.Items[2] is not TabItem pdb)
            return;

        TargetDiagnostics.Add(window, new object());
        window.BuildLogsDiagnosticTargetView(logs);
        window.BuildLiveTimelineTargetView(timeline);
        window.BuildPdbTargetView(pdb);
    }

    private void BuildLogsDiagnosticTargetView(TabItem tab)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(CreateDiagnosticHeader(
            "\uE8A5", "Logs & Diagnose",
            "Rekursive Service-Logs, Vollanalyse, Reparaturhinweise und Live-Monitoring",
            ("\uE9D9", "Vollanalyse", "AnalyzeCommand", true),
            ("\uE74D", "Bericht exportieren", "ExportReportCommand", false)));

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.84, GridUnitType.Star), MinWidth = 330 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.16, GridUnitType.Star), MinWidth = 420 });
        Grid.SetRow(body, 1);

        var left = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.25, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.75, GridUnitType.Star) });
        left.Children.Add(CreateLogViewerCard());
        var activity = CreateTextLogCard("Aktivitäts-/Smart-Start-Protokoll", "ActivityText", 10);
        Grid.SetRow(activity, 1);
        left.Children.Add(activity);
        body.Children.Add(left);

        var diagnosis = CreateDiagnosisCard();
        Grid.SetColumn(diagnosis, 1);
        body.Children.Add(diagnosis);
        root.Children.Add(body);
        tab.Content = root;
    }

    private Border CreateLogViewerCard()
    {
        var border = CreateDiagnosticCard();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var heading = new Grid { Margin = new Thickness(12, 9, 12, 5) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "Live-/Dateilog", FontSize = 15, FontWeight = FontWeights.SemiBold });
        var inventory = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        inventory.SetBinding(TextBlock.TextProperty, new Binding("LogInventoryText"));
        Grid.SetColumn(inventory, 1);
        heading.Children.Add(inventory);
        root.Children.Add(heading);

        var controls = new WrapPanel { Margin = new Thickness(9, 0, 9, 7) };
        var logs = new ComboBox { MinWidth = 250, Width = 310, Margin = new Thickness(3) };
        logs.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("LogFiles"));
        logs.SetBinding(ComboBox.SelectedItemProperty, new Binding("SelectedLogPath") { Mode = BindingMode.TwoWay });
        controls.Children.Add(logs);
        controls.Children.Add(CreateDiagnosticCommandButton("\uE72C", "Log neu laden", "ReloadLogCommand"));
        controls.Children.Add(CreateDiagnosticCommandButton("\uE768", "Live Start", "StartLiveCommand", true));
        controls.Children.Add(CreateDiagnosticCommandButton("\uE71A", "Live Stop", "StopLiveCommand"));
        var state = new TextBlock { Foreground = (Brush)FindResource("MutedStrong"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        state.SetBinding(TextBlock.TextProperty, new Binding("LiveStatusText"));
        controls.Children.Add(state);
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        var log = CreateReadOnlyLogBox("LogText", 11);
        Grid.SetRow(log, 2);
        root.Children.Add(log);
        border.Child = root;
        return border;
    }

    private Border CreateDiagnosisCard()
    {
        var border = CreateDiagnosticCard();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });

        var heading = new Grid { Margin = new Thickness(12, 8, 9, 6) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "Diagnose", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(CreateDiagnosticCommandButton("\uE9D9", "Vollanalyse", "AnalyzeCommand", true));
        actions.Children.Add(CreateDiagnosticCommandButton("\uE73E", "Reparatur anwenden", "ApplyRepairCommand"));
        actions.Children.Add(CreateDiagnosticCommandButton("\uE74D", "Export", "ExportReportCommand"));
        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        root.Children.Add(heading);

        var table = new DataGrid { AlternationCount = 2, RowHeight = 32, ColumnHeaderHeight = 32, BorderThickness = new Thickness(0, 1, 0, 1) };
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Diagnostics"));
        table.SetBinding(DataGrid.SelectedItemProperty, new Binding("SelectedIssue") { Mode = BindingMode.TwoWay });
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        table.Columns.Add(new DataGridTextColumn { Header = "Schwere", Binding = new Binding("SeverityText"), Width = new DataGridLength(76) });
        table.Columns.Add(new DataGridTextColumn { Header = "Code", Binding = new Binding("Code"), Width = new DataGridLength(118) });
        table.Columns.Add(new DataGridTextColumn { Header = "Titel", Binding = new Binding("Title"), Width = new DataGridLength(210) });
        table.Columns.Add(new DataGridTextColumn { Header = "Dienst", Binding = new Binding("ServiceName"), Width = new DataGridLength(105) });
        table.Columns.Add(new DataGridTextColumn { Header = "Konfidenz", Binding = new Binding("ConfidenceText"), Width = new DataGridLength(84) });
        table.Columns.Add(new DataGridTextColumn { Header = "Quelle", Binding = new Binding("Source"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(table, 1);
        root.Children.Add(table);

        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = "Auswertung / Reparaturanweisung", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        details.Children.Add(CreateDiagnosticBoundText("SelectedIssue.Description", 11, (Brush)FindResource("Text")));
        details.Children.Add(CreateDiagnosticLabel("Empfehlung"));
        details.Children.Add(CreateDiagnosticBoundText("SelectedIssue.Recommendation", 10, (Brush)FindResource("MutedStrong")));
        details.Children.Add(CreateDiagnosticLabel("Beleg"));
        var evidence = CreateDiagnosticBoundText("SelectedIssue.Evidence", 10, (Brush)FindResource("Muted"));
        evidence.FontFamily = new FontFamily("Consolas");
        details.Children.Add(evidence);
        var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12, 8, 12, 8), Content = details };
        Grid.SetRow(detailScroll, 2);
        root.Children.Add(detailScroll);
        border.Child = root;
        return border;
    }

    private void BuildLiveTimelineTargetView(TabItem tab)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.7, GridUnitType.Star) });
        root.Children.Add(CreateDiagnosticHeader(
            "\uE823", "Live Server Timeline",
            "Korrelierte Log-, Prozess-, Port- und Service-Ereignisse in zeitlicher Reihenfolge",
            ("\uE768", "Monitor starten", "StartLiveCommand", true),
            ("\uE71A", "Monitor stoppen", "StopLiveCommand", false),
            ("\uE74D", "Bericht exportieren", "ExportReportCommand", false)));

        var live = CreateTimelineEventsCard();
        Grid.SetRow(live, 1);
        root.Children.Add(live);
        var transitions = CreateTimelineTransitionsCard();
        Grid.SetRow(transitions, 2);
        root.Children.Add(transitions);
        tab.Content = root;
    }

    private Border CreateTimelineEventsCard()
    {
        var border = CreateDiagnosticCard(new Thickness(0, 0, 0, 8));
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var heading = new Grid { Margin = new Thickness(12, 8, 9, 6) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "Live-Ereignisse", FontSize = 14, FontWeight = FontWeights.SemiBold });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var state = new TextBlock { Foreground = (Brush)FindResource("MutedStrong"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        state.SetBinding(TextBlock.TextProperty, new Binding("LiveStatusText"));
        actions.Children.Add(state);
        actions.Children.Add(CreateDiagnosticCommandButton("\uE74D", "Timeline leeren", "ClearLiveCommand"));
        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        root.Children.Add(heading);

        var table = new DataGrid { AlternationCount = 2, RowHeight = 31, ColumnHeaderHeight = 32, BorderThickness = new Thickness(0, 1, 0, 0) };
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("LiveEvents"));
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        table.Columns.Add(new DataGridTextColumn { Header = "Zeit", Binding = new Binding("Timestamp") { StringFormat = "HH:mm:ss" }, Width = new DataGridLength(82) });
        table.Columns.Add(new DataGridTextColumn { Header = "Schwere", Binding = new Binding("Severity"), Width = new DataGridLength(82) });
        table.Columns.Add(new DataGridTextColumn { Header = "Code", Binding = new Binding("Code"), Width = new DataGridLength(120) });
        table.Columns.Add(new DataGridTextColumn { Header = "Quelle", Binding = new Binding("Source"), Width = new DataGridLength(125) });
        table.Columns.Add(new DataGridTextColumn { Header = "DEP", Binding = new Binding("Department"), Width = new DataGridLength(55) });
        table.Columns.Add(new DataGridTextColumn { Header = "CMD", Binding = new Binding("Command"), Width = new DataGridLength(55) });
        table.Columns.Add(new DataGridTextColumn { Header = "Len", Binding = new Binding("PacketLength"), Width = new DataGridLength(55) });
        table.Columns.Add(new DataGridTextColumn { Header = "Meldung", Binding = new Binding("Message"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(table, 1);
        root.Children.Add(table);
        border.Child = root;
        return border;
    }

    private Border CreateTimelineTransitionsCard()
    {
        var border = CreateDiagnosticCard();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock { Text = "Service-/Prozess-Transitions", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 8, 12, 6) });
        var table = new DataGrid { AlternationCount = 2, RowHeight = 30, ColumnHeaderHeight = 31, BorderThickness = new Thickness(0, 1, 0, 0) };
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("RuntimeTransitions"));
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        table.Columns.Add(new DataGridTextColumn { Header = "Zeit", Binding = new Binding("TimeText"), Width = new DataGridLength(80) });
        table.Columns.Add(new DataGridTextColumn { Header = "Dienst", Binding = new Binding("DisplayName"), Width = new DataGridLength(120) });
        table.Columns.Add(new DataGridTextColumn { Header = "Statuswechsel", Binding = new Binding("StateText"), Width = new DataGridLength(180) });
        table.Columns.Add(new DataGridTextColumn { Header = "PID vorher", Binding = new Binding("PreviousPid"), Width = new DataGridLength(85) });
        table.Columns.Add(new DataGridTextColumn { Header = "PID nachher", Binding = new Binding("CurrentPid"), Width = new DataGridLength(85) });
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "Port offen", Binding = new Binding("PortOpen"), Width = new DataGridLength(80) });
        table.Columns.Add(new DataGridTextColumn { Header = "Erklärung", Binding = new Binding("Reason"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(table, 1);
        root.Children.Add(table);
        border.Child = root;
        return border;
    }

    private void BuildPdbTargetView(TabItem tab)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.72, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.28, GridUnitType.Star) });
        root.Children.Add(CreateDiagnosticHeader(
            "PDB", "PDB / Symbole",
            "Lokale Symbolindexierung und Suche; Binaries werden nicht verändert",
            ("\uE895", "PDBs indexieren", "IndexPdbCommand", true)));

        var status = CreatePdbStatusCard();
        Grid.SetRow(status, 1);
        root.Children.Add(status);
        var search = CreatePdbSearchCard();
        Grid.SetRow(search, 2);
        root.Children.Add(search);
        tab.Content = root;
    }

    private Border CreatePdbStatusCard()
    {
        var border = CreateDiagnosticCard(new Thickness(0, 0, 0, 8));
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock { Text = "Symbolindex", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 8, 12, 6) });
        var table = new DataGrid { RowHeight = 31, ColumnHeaderHeight = 32, BorderThickness = new Thickness(0, 1, 0, 0) };
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("PdbStatuses"));
        table.Columns.Add(new DataGridTextColumn { Header = "PDB", Binding = new Binding("Name"), Width = new DataGridLength(190) });
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "Vorhanden", Binding = new Binding("Exists"), Width = new DataGridLength(90) });
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "Indexiert", Binding = new Binding("Indexed"), Width = new DataGridLength(90) });
        table.Columns.Add(new DataGridTextColumn { Header = "Details", Binding = new Binding("Detail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(table, 1);
        root.Children.Add(table);
        border.Child = root;
        return border;
    }

    private Border CreatePdbSearchCard()
    {
        var border = CreateDiagnosticCard();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var controls = new Grid { Margin = new Thickness(10, 8, 10, 7) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var query = new TextBox { ToolTip = "Symbolname oder Teilstring eingeben", VerticalContentAlignment = VerticalAlignment.Center };
        query.SetBinding(TextBox.TextProperty, new Binding("PdbSearch") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        query.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || DataContext is not MainViewModel vm)
                return;
            if (vm.SearchPdbCommand.CanExecute(null))
                vm.SearchPdbCommand.Execute(null);
            args.Handled = true;
        };
        controls.Children.Add(query);

        var search = CreateDiagnosticCommandButton("\uE721", "Symbole suchen", "SearchPdbCommand", true);
        search.Margin = new Thickness(7, 0, 7, 0);
        Grid.SetColumn(search, 1);
        controls.Children.Add(search);
        var hint = new TextBlock { Text = "z.B. CParserZone, GUILDWARSTATUS, WorldManagerSession", Foreground = (Brush)FindResource("Muted"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(hint, 2);
        controls.Children.Add(hint);
        root.Children.Add(controls);

        var output = CreateReadOnlyLogBox("PdbOutput", 11);
        Grid.SetRow(output, 1);
        root.Children.Add(output);
        border.Child = root;
        return border;
    }

    private Border CreateDiagnosticHeader(string glyph, string title, string subtitle,
        params (string Glyph, string Label, string Command, bool Primary)[] actions)
    {
        var border = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 9, 12, 9) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new TextBlock { Text = glyph, FontFamily = glyph.Length == 1 ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Segoe UI"), Foreground = (Brush)FindResource("Cyan"), FontSize = glyph.Length == 1 ? 23 : 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        heading.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionTitle"), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(heading);
        left.Children.Add(new TextBlock { Text = subtitle, Foreground = (Brush)FindResource("Muted"), FontSize = 10, Margin = new Thickness(42, 2, 16, 0), TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(left);
        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions)
            panel.Children.Add(CreateDiagnosticCommandButton(action.Glyph, action.Label, action.Command, action.Primary));
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        border.Child = grid;
        return border;
    }

    private Button CreateDiagnosticCommandButton(string glyph, string label, string commandPath, bool primary = false)
    {
        var button = new Button
        {
            Content = CreateButtonContent(glyph, label, 11),
            Style = primary ? (Style)FindResource("PrimaryActionButton") : (Style)FindResource(typeof(Button)),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(2),
            ToolTip = label
        };
        button.SetBinding(Button.CommandProperty, new Binding(commandPath));
        return button;
    }

    private Border CreateDiagnosticCard(Thickness? margin = null)
        => new()
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(0),
            Margin = margin ?? new Thickness(0)
        };

    private Border CreateTextLogCard(string title, string path, double fontSize)
    {
        var border = CreateDiagnosticCard();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 8, 12, 6) });
        var text = CreateReadOnlyLogBox(path, fontSize);
        Grid.SetRow(text, 1);
        root.Children.Add(text);
        border.Child = root;
        return border;
    }

    private TextBox CreateReadOnlyLogBox(string path, double fontSize)
    {
        var box = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = fontSize,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(9, 7, 9, 7)
        };
        box.SetBinding(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        return box;
    }

    private TextBlock CreateDiagnosticBoundText(string path, double fontSize, Brush foreground)
    {
        var text = new TextBlock { FontSize = fontSize, Foreground = foreground, TextWrapping = TextWrapping.Wrap };
        text.SetBinding(TextBlock.TextProperty, new Binding(path) { TargetNullValue = "–" });
        return text;
    }

    private TextBlock CreateDiagnosticLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("MutedStrong"),
            Margin = new Thickness(0, 7, 0, 2)
        };
}
