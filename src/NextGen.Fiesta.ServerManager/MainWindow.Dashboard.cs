using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<MainWindow, object> TargetDashboards = new();
    private static readonly bool DashboardLoadedHookRegistered = RegisterDashboardLoadedHook();

    private static bool RegisterDashboardLoadedHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnDashboardWindowLoaded));
        return true;
    }

    private static void OnDashboardWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || TargetDashboards.TryGetValue(window, out _))
        {
            return;
        }

        if (window._mainNavigation is null || window._mainNavigation.Items.Count == 0)
        {
            return;
        }

        if (window._mainNavigation.Items[0] is not TabItem dashboard)
        {
            return;
        }

        TargetDashboards.Add(window, new object());
        window.BuildDashboardTargetView(dashboard);
    }

    private void BuildDashboardTargetView(TabItem dashboard)
    {
        var root = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = CreateDashboardHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var cards = new UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(0, 0, 0, 8)
        };
        cards.Children.Add(CreateDashboardMetricCard("Server Health", "\uE9D9", (Brush)FindResource("Good"), "HealthScore", "{}{0}%"));
        cards.Children.Add(CreateDashboardMetricCard("Dienste erkannt", "\uE950", (Brush)FindResource("Cyan"), "Services.Count"));
        cards.Children.Add(CreateDashboardMetricCard("Ausgewählter Dienst", "\uE8A7", (Brush)FindResource("Accent"), "SelectedService.DisplayName"));
        cards.Children.Add(CreateDashboardMetricCard("Clients (Auswahl)", "\uE716", BrushFrom("#A94DFF"), "SelectedService.EstablishedClientConnections", "{}{0:N0}"));
        Grid.SetRow(cards, 1);
        root.Children.Add(cards);

        var content = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.42, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star), MinWidth = 300 });

        var services = CreateDashboardServicesTable();
        Grid.SetColumn(services, 0);
        content.Children.Add(services);

        var selected = CreateDashboardSelectedServicePanel();
        Grid.SetColumn(selected, 1);
        content.Children.Add(selected);

        Grid.SetRow(content, 2);
        root.Children.Add(content);

        var orchestration = CreateDashboardOrchestrationPanel();
        Grid.SetRow(orchestration, 3);
        root.Children.Add(orchestration);

        dashboard.Content = root;
    }

    private Border CreateDashboardHeader()
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 9, 12, 9)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = "\uE80F",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Foreground = (Brush)FindResource("Cyan"),
            FontSize = 24,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        title.Children.Add(new TextBlock
        {
            Text = "Serverübersicht",
            Style = (Style)FindResource("SectionTitle"),
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(title);

        var subtitle = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 11,
            Margin = new Thickness(42, 2, 20, 0),
            TextWrapping = TextWrapping.Wrap
        };
        subtitle.SetBinding(TextBlock.TextProperty, new Binding("StatusLine"));
        left.Children.Add(subtitle);
        grid.Children.Add(left);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var scan = new Button
        {
            Content = CreateButtonContent("\uE721", "Server scannen"),
            Padding = new Thickness(14, 7, 14, 7)
        };
        scan.SetBinding(Button.CommandProperty, new Binding("ScanCommand"));
        actions.Children.Add(scan);

        var refresh = new Button
        {
            Content = CreateButtonContent("\uE72C", "Status aktualisieren"),
            Style = (Style)FindResource("PrimaryActionButton"),
            Padding = new Thickness(15, 7, 15, 7),
            Margin = new Thickness(7, 3, 0, 3)
        };
        refresh.SetBinding(Button.CommandProperty, new Binding("RefreshCommand"));
        actions.Children.Add(refresh);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        border.Child = grid;
        return border;
    }

    private Border CreateDashboardMetricCard(
        string label,
        string glyph,
        Brush accent,
        string bindingPath,
        string? stringFormat = null)
    {
        var card = new Border
        {
            Style = (Style)FindResource("SummaryCard"),
            Margin = new Thickness(0, 0, 7, 0),
            Height = 68
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new Border
        {
            Width = 4,
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-11, -9, 0, -9)
        });
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Foreground = accent,
            FontSize = 23,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("MutedStrong"),
            FontSize = 10
        });
        var value = new TextBlock
        {
            Foreground = (Brush)FindResource("Text"),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };
        var binding = new Binding(bindingPath)
        {
            TargetNullValue = "–",
            FallbackValue = "–"
        };
        if (!string.IsNullOrWhiteSpace(stringFormat))
        {
            binding.StringFormat = stringFormat;
        }
        value.SetBinding(TextBlock.TextProperty, binding);
        text.Children.Add(value);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        card.Child = grid;
        return card;
    }

    private Border CreateDashboardServicesTable()
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var heading = new Grid { Margin = new Thickness(12, 9, 12, 7) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "Dienste & Laufzeitstatus",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        });
        var hint = new TextBlock
        {
            Text = "Dienst auswählen für Aktionen und Details",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(hint, 1);
        heading.Children.Add(hint);
        root.Children.Add(heading);

        var table = new DataGrid
        {
            AlternationCount = 2,
            RowHeight = 32,
            ColumnHeaderHeight = 32,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0)
        };
        table.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Services"));
        table.SetBinding(DataGrid.SelectedItemProperty, new Binding("SelectedService") { Mode = BindingMode.TwoWay });
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto);

        table.Columns.Add(new DataGridTextColumn { Header = "Health", Binding = new Binding("HealthSymbol"), Width = new DataGridLength(58) });
        table.Columns.Add(new DataGridTextColumn { Header = "Dienst", Binding = new Binding("DisplayName"), Width = new DataGridLength(115) });
        table.Columns.Add(new DataGridTextColumn { Header = "Windows Service", Binding = new Binding("ServiceName"), Width = new DataGridLength(130) });
        table.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("StatusText"), Width = new DataGridLength(135) });
        table.Columns.Add(new DataGridTextColumn { Header = "Port", Binding = new Binding("ClientPort"), Width = new DataGridLength(68) });
        table.Columns.Add(new DataGridTextColumn { Header = "Clients", Binding = new Binding("EstablishedClientConnections"), Width = new DataGridLength(68) });
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "Offen", Binding = new Binding("PortOpen"), Width = new DataGridLength(62) });
        table.Columns.Add(new DataGridTextColumn { Header = "PID", Binding = new Binding("ProcessId"), Width = new DataGridLength(68) });
        table.Columns.Add(new DataGridTextColumn { Header = "CPU", Binding = new Binding("CpuText"), Width = new DataGridLength(76) });
        table.Columns.Add(new DataGridTextColumn { Header = "RAM", Binding = new Binding("MemoryText"), Width = new DataGridLength(92) });
        table.Columns.Add(new DataGridTextColumn { Header = "Handles", Binding = new Binding("HandleCount"), Width = new DataGridLength(76) });
        table.Columns.Add(new DataGridTextColumn { Header = "Startzeit", Binding = new Binding("ProcessStartTime") { StringFormat = "HH:mm:ss" }, Width = new DataGridLength(82) });

        Grid.SetRow(table, 1);
        root.Children.Add(table);
        border.Child = root;
        return border;
    }

    private Border CreateDashboardSelectedServicePanel()
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(12)
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var stack = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameBlock = new StackPanel();
        nameBlock.Children.Add(new TextBlock
        {
            Text = "Ausgewählter Dienst",
            Foreground = (Brush)FindResource("MutedStrong"),
            FontSize = 10
        });
        var name = new TextBlock
        {
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 8, 0)
        };
        name.SetBinding(TextBlock.TextProperty, new Binding("SelectedService.DisplayName") { TargetNullValue = "Kein Dienst gewählt" });
        nameBlock.Children.Add(name);
        top.Children.Add(nameBlock);

        var statusPill = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 72
        };
        statusPill.SetBinding(Border.BackgroundProperty, new Binding("SelectedService.State") { Converter = new StateBrushConverter() });
        var status = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        status.SetBinding(TextBlock.TextProperty, new Binding("SelectedService.StatusText") { TargetNullValue = "–" });
        statusPill.Child = status;
        Grid.SetColumn(statusPill, 1);
        top.Children.Add(statusPill);
        stack.Children.Add(top);

        stack.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 8) });
        stack.Children.Add(CreateDashboardDetail("Windows Service", "SelectedService.ServiceName"));
        stack.Children.Add(CreateDashboardDetail("EXE", "SelectedService.ExecutablePath", true));
        stack.Children.Add(CreateDashboardDetail("Config", "SelectedService.ConfigPath", true));
        stack.Children.Add(CreateDashboardDetail("Ports", "SelectedService.ClientPort", false, "Client {0}"));
        stack.Children.Add(CreateDashboardDetail("CPU", "SelectedService.CpuText"));
        stack.Children.Add(CreateDashboardDetail("RAM", "SelectedService.MemoryText"));

        stack.Children.Add(new TextBlock
        {
            Text = "Serviceaktionen",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 5)
        });

        var actions = new UniformGrid { Columns = 2 };
        actions.Children.Add(CreateDashboardCommandButton("\uE768", "Start", "StartCommand", primary: true));
        actions.Children.Add(CreateDashboardCommandButton("\uE71A", "Stop", "StopCommand"));
        actions.Children.Add(CreateDashboardCommandButton("\uE72C", "Neustart", "RestartCommand"));
        actions.Children.Add(CreateDashboardCommandButton("\uE90F", "Recovery setzen", "ConfigureRecoveryCommand"));
        actions.Children.Add(CreateDashboardCommandButton("\uE896", "Neu installieren", "ReinstallCommand"));
        actions.Children.Add(CreateDashboardCommandButton("\uE74D", "Dienst löschen", "UninstallCommand"));
        actions.Children.Add(CreateDashboardCommandButton("\uE8B7", "Ordner öffnen", "OpenFolderCommand"));
        actions.Children.Add(CreateDashboardCommandButton("\uE8A5", "Config öffnen", "OpenConfigCommand"));
        stack.Children.Add(actions);

        scroll.Content = stack;
        border.Child = scroll;
        return border;
    }

    private FrameworkElement CreateDashboardDetail(string label, string bindingPath, bool wrap = false, string? stringFormat = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 9
        });
        var value = new TextBlock
        {
            FontSize = 11,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        var binding = new Binding(bindingPath) { TargetNullValue = "–", FallbackValue = "–" };
        if (!string.IsNullOrWhiteSpace(stringFormat))
        {
            binding.StringFormat = stringFormat;
        }
        value.SetBinding(TextBlock.TextProperty, binding);
        stack.Children.Add(value);
        return stack;
    }

    private Button CreateDashboardCommandButton(string glyph, string label, string commandPath, bool primary = false)
    {
        var button = new Button
        {
            Content = CreateButtonContent(glyph, label, 11),
            Style = primary ? (Style)FindResource("PrimaryActionButton") : (Style)FindResource(typeof(Button)),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(2)
        };
        button.SetBinding(Button.CommandProperty, new Binding(commandPath));
        return button;
    }

    private Border CreateDashboardOrchestrationPanel()
    {
        var border = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Padding = new Thickness(11, 8, 11, 8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = "Smart Orchestration",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = "Startet und stoppt die NA2016-Komponenten in der bekannten Abhängigkeitsreihenfolge und prüft Readiness/Ports.",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            Margin = new Thickness(0, 2, 16, 0),
            TextWrapping = TextWrapping.Wrap
        });
        grid.Children.Add(text);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(CreateDashboardCommandButton("\uE768", "Smart Start", "SmartStartCommand", primary: true));
        actions.Children.Add(CreateDashboardCommandButton("\uE71A", "Smart Stop", "SmartStopCommand"));
        actions.Children.Add(CreateDashboardCommandButton("\uE72C", "Smart Restart", "SmartRestartCommand"));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        border.Child = grid;
        return border;
    }
}
