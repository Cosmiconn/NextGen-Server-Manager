using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<MainWindow, object> TargetServerModules = new();
    private static readonly bool ServerModulesLoadedHookRegistered = RegisterServerModulesLoadedHook();

    private static bool RegisterServerModulesLoadedHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnServerModulesWindowLoaded));
        return true;
    }

    private static void OnServerModulesWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || TargetServerModules.TryGetValue(window, out _))
            return;

        var mainNavigation = window._mainNavigation;
        if (mainNavigation is null || mainNavigation.Items.Count < 2 ||
            mainNavigation.Items[1] is not TabItem serverTab ||
            serverTab.Content is not TabControl serverNavigation ||
            serverNavigation.Items.Count < 4 ||
            serverNavigation.Items[1] is not TabItem limits ||
            serverNavigation.Items[2] is not TabItem adaptive ||
            serverNavigation.Items[3] is not TabItem performance)
            return;

        TargetServerModules.Add(window, new object());
        window.BuildLimitsTargetView(limits);
        window.BuildAdaptiveHooksTargetView(adaptive);
        window.BuildPerformanceTargetView(performance);
    }

    private void BuildLimitsTargetView(TabItem tab)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.86, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.14, GridUnitType.Star) });

        root.Children.Add(CreateDiagnosticHeader(
            "\uE9D9",
            "NA2016 Limit- und Map-Audit",
            "Verifizierte Kapazitätsgrenzen, Binär-Evidenz und BlockInfo/SHBD-Kartenanalyse",
            ("\uE72C", "Limits/Maps neu analysieren", "AnalyzeCapacityCommand", true)));

        var limitsCard = CreateModuleTableCard("Verifizierte Limits", "CapacityLimits", table =>
        {
            table.Columns.Add(ModuleTextColumn("Scope", "Scope", 100));
            table.Columns.Add(ModuleTextColumn("Kategorie", "Category", 115));
            table.Columns.Add(ModuleTextColumn("Limit", "Name", 155));
            table.Columns.Add(ModuleTextColumn("Hard Limit", "LimitText", 95));
            table.Columns.Add(ModuleTextColumn("Objektgröße", "ObjectSizeText", 100));
            table.Columns.Add(ModuleTextColumn("Pool ~RAM", "PoolMemoryText", 95));
            table.Columns.Add(ModuleTextColumn("Binäre Evidenz", "Evidence", 270));
            table.Columns.Add(ModuleTextColumn("Einordnung", "Notes", new DataGridLength(1, DataGridLengthUnitType.Star)));
        }, "CapacitySummary");
        Grid.SetRow(limitsCard, 1);
        root.Children.Add(limitsCard);

        var mapsCard = CreateModuleTableCard("Karten / BlockInfo", "MapCapacities", table =>
        {
            table.Columns.Add(ModuleTextColumn("MapID", "MapId", 120));
            table.Columns.Add(ModuleTextColumn("Name", "MapName", 175));
            table.Columns.Add(ModuleTextColumn("Zone", "Zones", 70));
            table.Columns.Add(ModuleTextColumn("Field x/y", "FieldSizeText", 105));
            table.Columns.Add(ModuleTextColumn("Field ~Welt", "FieldWorldSizeText", 135));
            table.Columns.Add(ModuleTextColumn("SHBD Header", "ShbdHeaderText", 110));
            table.Columns.Add(ModuleTextColumn("Kollisionsgrid", "CollisionGridText", 135));
            table.Columns.Add(ModuleTextColumn("Server-Blockfläche", "ServerBlockWorldSizeText", 150));
            table.Columns.Add(ModuleTextColumn("SHBD", "FileSizeText", 85));
            table.Columns.Add(ModuleTextColumn("Status", "Status", new DataGridLength(1, DataGridLengthUnitType.Star)));
        });
        Grid.SetRow(mapsCard, 2);
        root.Children.Add(mapsCard);
        tab.Content = root;
    }

    private void BuildAdaptiveHooksTargetView(TabItem tab)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(CreateDiagnosticHeader(
            "\uE713",
            "Adaptive Hooks",
            "Konfigurieren, messen und nur build-/hashgebunden freigegebene Änderungen anwenden",
            ("\uE72C", "Neu bewerten", "AnalyzeAdaptiveHooksCommand", false),
            ("\uE768", "Hook-Profil anwenden", "ApplyAdaptiveHooksCommand", true),
            ("\uE777", "Letztes Backup", "RestoreAdaptiveHooksCommand", false)));

        var config = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var configStack = new StackPanel();

        var profileRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        profileRow.Children.Add(new TextBlock
        {
            Text = "Profile",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        profileRow.Children.Add(CreateHookProfileButton("Sicher", "safe"));
        profileRow.Children.Add(CreateHookProfileButton("Ausgewogen", "balanced", true));
        profileRow.Children.Add(CreateHookProfileButton("Leistung / Research", "high"));
        var summary = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        summary.SetBinding(TextBlock.TextProperty, new Binding("AdaptiveHookSummary"));
        profileRow.Children.Add(summary);
        configStack.Children.Add(profileRow);

        var values = new WrapPanel();
        values.Children.Add(CreateHookField("WM Spieler", "HookWmClientTarget", 82));
        values.Children.Add(CreateHookField("WM S2S", "HookWmZoneTarget", 74));
        values.Children.Add(CreateHookField("Zone Listener", "HookZoneClientTarget", 92));
        values.Children.Add(CreateHookField("ShinePlayer", "HookZonePlayerTarget", 88));
        values.Children.Add(CreateHookField("ShineMob", "HookZoneMobTarget", 78));
        values.Children.Add(CreateHookField("ShineNPC", "HookZoneNpcTarget", 78));
        values.Children.Add(CreateHookField("CPU Warn %", "HookCpuWarnTarget", 78));
        values.Children.Add(CreateHookField("CPU Block %", "HookCpuBlockTarget", 78));
        values.Children.Add(CreateHookField("RAM Warn %", "HookMemoryWarnTarget", 78));
        values.Children.Add(CreateHookField("RAM Block %", "HookMemoryBlockTarget", 78));
        configStack.Children.Add(values);

        var experimental = new CheckBox
        {
            Content = "Experimentelle Zone-Binary-Hooks anfordern",
            Margin = new Thickness(3, 7, 3, 0),
            ToolTip = "Unvollständig verifizierte Handle-Rebases bleiben unabhängig davon gesperrt."
        };
        experimental.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding("AllowExperimentalZoneBinaryHooks") { Mode = BindingMode.TwoWay });
        configStack.Children.Add(experimental);
        config.Child = configStack;
        Grid.SetRow(config, 1);
        root.Children.Add(config);

        var assessments = CreateModuleTableCard("Hook-Bewertungen", "AdaptiveHookAssessments", table =>
        {
            table.Columns.Add(ModuleTextColumn("Scope", "Scope", 95));
            table.Columns.Add(ModuleTextColumn("Ressource", "Resource", 150));
            table.Columns.Add(ModuleTextColumn("Hook", "HookType", 145));
            table.Columns.Add(ModuleTextColumn("Aktuell", "CurrentText", 75));
            table.Columns.Add(ModuleTextColumn("Ziel", "TargetText", 75));
            table.Columns.Add(ModuleTextColumn("CPU", "CpuText", 80));
            table.Columns.Add(ModuleTextColumn("RAM aktuell → proj.", "MemoryText", 155));
            table.Columns.Add(ModuleTextColumn("Entscheidung", "Decision", 100));
            table.Columns.Add(ModuleTextColumn("Anwendbar", "ApplyText", 90));
            table.Columns.Add(ModuleTextColumn("Begründung / Warnung", "Reason", new DataGridLength(1, DataGridLengthUnitType.Star)));
        });
        Grid.SetRow(assessments, 2);
        root.Children.Add(assessments);

        var warning = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(10, 7, 10, 7)
        };
        warning.Child = new TextBlock
        {
            Text = "Schutzlogik bleibt aktiv: CPU-/RAM-Blockschwellen, exakte Baseline-Hashes und abhängige Zone-Objekthandle-Basen können Änderungen sperren. Ein freigegebenes Profil ist keine pauschale Binary-Patch-Freigabe.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(warning, 3);
        root.Children.Add(warning);
        tab.Content = root;
    }

    private void BuildPerformanceTargetView(TabItem tab)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.9, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) });

        var header = CreateDiagnosticHeader(
            "\uE9D9",
            "Performance / Vertical Scaling",
            "Weniger Zonen, mehr Leistung pro Prozess – CPU/Mainthread und Speichergrenzen getrennt bewerten",
            ("\uE72C", "Performance neu analysieren", "AnalyzePerformanceTuningCommand", true));
        root.Children.Add(header);

        var processes = CreateModuleTableCard("Prozess-Skalierung", "ProcessScaling", table =>
        {
            table.Columns.Add(ModuleTextColumn("Komponente", "Component", 135));
            table.Columns.Add(ModuleTextColumn("Architektur", "ArchitectureText", 100));
            table.Columns.Add(ModuleTextColumn("Threadmodell", "ThreadModelText", 135));
            table.Columns.Add(ModuleTextColumn("CPU", "CpuText", 80));
            table.Columns.Add(ModuleTextColumn("RAM", "MemoryText", 190));
            table.Columns.Add(ModuleTextColumn("Threads", "ThreadCount", 65));
            table.Columns.Add(ModuleTextColumn("Handles", "HandleCount", 70));
            table.Columns.Add(ModuleTextColumn("Sessions", "SessionText", 90));
            table.Columns.Add(ModuleTextColumn("CPU-Modell ~80%", "EstimatedCpuCeilingText", 115));
            table.Columns.Add(ModuleTextColumn("Vertikale Reserve", "VerticalHeadroom", 150));
            table.Columns.Add(ModuleTextColumn("Empfehlung", "Recommendation", new DataGridLength(1, DataGridLengthUnitType.Star)));
        }, "PerformanceTuningSummary");
        Grid.SetRow(processes, 1);
        root.Children.Add(processes);

        var candidates = CreateModuleTableCard("Tuning-Kandidaten / geplante Zielgrößen", "PerformanceTuningCandidates", table =>
        {
            table.Columns.Add(ModuleTextColumn("Scope", "Scope", 95));
            table.Columns.Add(ModuleTextColumn("Ressource", "Resource", 145));
            table.Columns.Add(ModuleTextColumn("Stock", "CurrentText", 75));
            table.Columns.Add(ModuleTextColumn("Ziel", "TargetText", 75));
            table.Columns.Add(ModuleTextColumn("Rohspeicher", "MemoryText", 105));
            table.Columns.Add(ModuleTextColumn("Risiko", "Risk", 95));
            table.Columns.Add(ModuleTextColumn("Methode", "Method", 240));
            table.Columns.Add(ModuleTextColumn("Wirkung", "Impact", 270));
            table.Columns.Add(ModuleTextColumn("Beleg", "Evidence", new DataGridLength(1, DataGridLengthUnitType.Star)));
        });
        Grid.SetRow(candidates, 2);
        root.Children.Add(candidates);
        tab.Content = root;
    }

    private Button CreateHookProfileButton(string label, string parameter, bool primary = false)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(2),
            Style = primary ? (Style)FindResource("PrimaryActionButton") : (Style)FindResource(typeof(Button)),
            CommandParameter = parameter
        };
        button.SetBinding(Button.CommandProperty, new Binding("SetHookProfileCommand"));
        return button;
    }

    private FrameworkElement CreateHookField(string label, string path, double labelWidth)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(3, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Width = labelWidth,
            Foreground = (Brush)FindResource("MutedStrong"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });
        var input = new TextBox
        {
            Width = 62,
            FontSize = 11,
            Padding = new Thickness(6, 4, 6, 4)
        };
        input.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        panel.Children.Add(input);
        return panel;
    }

    private Border CreateModuleTableCard(string title, string itemsPath, Action<DataGrid> configure, string? summaryPath = null)
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(12, 8, 12, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(summaryPath))
        {
            var summary = new TextBlock
            {
                Foreground = (Brush)FindResource("Muted"),
                FontSize = 10,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            summary.SetBinding(TextBlock.TextProperty, new Binding(summaryPath));
            Grid.SetColumn(summary, 1);
            header.Children.Add(summary);
        }
        root.Children.Add(header);

        var table = new DataGrid
        {
            AlternationCount = 2,
            RowHeight = 31,
            ColumnHeaderHeight = 32,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(itemsPath));
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        configure(table);
        Grid.SetRow(table, 1);
        root.Children.Add(table);
        border.Child = root;
        return border;
    }

    private static DataGridTextColumn ModuleTextColumn(string header, string path, double width)
        => ModuleTextColumn(header, path, new DataGridLength(width));

    private static DataGridTextColumn ModuleTextColumn(string header, string path, DataGridLength width)
        => new()
        {
            Header = header,
            Binding = new Binding(path),
            Width = width
        };
}
