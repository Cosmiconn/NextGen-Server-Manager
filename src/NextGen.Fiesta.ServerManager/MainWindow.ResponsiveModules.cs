using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<MainWindow, object> ResponsiveModuleWindows = new();
    private static readonly bool ResponsiveModulesHookRegistered = RegisterResponsiveModulesHook();

    private static bool RegisterResponsiveModulesHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnResponsiveModulesLoaded));
        return true;
    }

    private static void OnResponsiveModulesLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || ResponsiveModuleWindows.TryGetValue(window, out _))
            return;

        ResponsiveModuleWindows.Add(window, new object());
        window.SizeChanged += (_, _) => window.Dispatcher.BeginInvoke(
            new Action(() => ApplyResponsiveModuleLayout(window)),
            DispatcherPriority.Background);
        window.Dispatcher.BeginInvoke(
            new Action(() => ApplyResponsiveModuleLayout(window)),
            DispatcherPriority.ContextIdle);
    }

    private static void ApplyResponsiveModuleLayout(MainWindow window)
    {
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        if (width <= 0 || window._mainNavigation is null)
            return;

        ApplyDashboardResponsiveLayout(window, width);
        ApplyLogsResponsiveLayout(window, width);
        ApplyCompactModuleActions(window, width);
    }

    private static void ApplyDashboardResponsiveLayout(MainWindow window, double width)
    {
        if (window._mainNavigation?.Items.Count < 1 ||
            window._mainNavigation.Items[0] is not TabItem dashboard ||
            dashboard.Content is not Grid root)
            return;

        var cards = ResponsiveDescendants<System.Windows.Controls.Primitives.UniformGrid>(root)
            .FirstOrDefault(grid =>
                grid.Children.Count == 4 &&
                ResponsiveDescendants<TextBlock>(grid).Any(text => text.Text == "Server Health"));
        if (cards is not null)
        {
            cards.Columns = width < 920 ? 2 : 4;
            foreach (var card in cards.Children.OfType<Border>())
                card.Margin = width < 920 ? new Thickness(0, 0, 7, 7) : new Thickness(0, 0, 7, 0);
        }

        var body = root.Children.OfType<Grid>()
            .FirstOrDefault(grid =>
                Grid.GetRow(grid) == 2 &&
                grid.ColumnDefinitions.Count == 2 &&
                ResponsiveDescendants<TextBlock>(grid).Any(text => text.Text == "Dienste & Laufzeitstatus"));
        if (body is null || body.Children.Count < 2)
            return;

        var servicePanel = body.Children[0];
        var selectedPanel = body.Children[1];
        if (width < 820)
        {
            EnsureTwoResponsiveRows(body);
            body.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            body.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumn(servicePanel, 0);
            Grid.SetRow(servicePanel, 0);
            Grid.SetColumn(selectedPanel, 0);
            Grid.SetRow(selectedPanel, 1);
            if (servicePanel is FrameworkElement first)
                first.Margin = new Thickness(0, 0, 0, 8);
        }
        else
        {
            body.RowDefinitions.Clear();
            body.ColumnDefinitions[0].Width = new GridLength(1.42, GridUnitType.Star);
            body.ColumnDefinitions[1].Width = new GridLength(0.58, GridUnitType.Star);
            Grid.SetColumn(servicePanel, 0);
            Grid.SetRow(servicePanel, 0);
            Grid.SetColumn(selectedPanel, 1);
            Grid.SetRow(selectedPanel, 0);
            if (servicePanel is FrameworkElement first)
                first.Margin = new Thickness(0, 0, 8, 0);
        }
    }

    private static void ApplyLogsResponsiveLayout(MainWindow window, double width)
    {
        if (window._mainNavigation?.Items.Count < 3 ||
            window._mainNavigation.Items[2] is not TabItem diagnostic ||
            diagnostic.Content is not TabControl diagnosticSub ||
            diagnosticSub.Items.Count < 1 ||
            diagnosticSub.Items[0] is not TabItem logs ||
            logs.Content is not Grid root)
            return;

        var body = root.Children.OfType<Grid>()
            .FirstOrDefault(grid =>
                Grid.GetRow(grid) == 1 &&
                grid.ColumnDefinitions.Count == 2 &&
                ResponsiveDescendants<TextBlock>(grid).Any(text => text.Text == "Live-/Dateilog") &&
                ResponsiveDescendants<TextBlock>(grid).Any(text => text.Text == "Diagnose"));
        if (body is null || body.Children.Count < 2)
            return;

        var logPanel = body.Children[0];
        var diagnosticPanel = body.Children[1];
        if (width < 860)
        {
            EnsureTwoResponsiveRows(body);
            body.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            body.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumn(logPanel, 0);
            Grid.SetRow(logPanel, 0);
            Grid.SetColumn(diagnosticPanel, 0);
            Grid.SetRow(diagnosticPanel, 1);
            if (logPanel is FrameworkElement first)
                first.Margin = new Thickness(0, 0, 0, 8);
        }
        else
        {
            body.RowDefinitions.Clear();
            body.ColumnDefinitions[0].Width = new GridLength(0.84, GridUnitType.Star);
            body.ColumnDefinitions[1].Width = new GridLength(1.16, GridUnitType.Star);
            Grid.SetColumn(logPanel, 0);
            Grid.SetRow(logPanel, 0);
            Grid.SetColumn(diagnosticPanel, 1);
            Grid.SetRow(diagnosticPanel, 0);
            if (logPanel is FrameworkElement first)
                first.Margin = new Thickness(0, 0, 8, 0);
        }

        var pdbHint = ResponsiveDescendants<TextBlock>(diagnosticSub)
            .FirstOrDefault(text => text.Text?.StartsWith("z.B. CParserZone", StringComparison.Ordinal) == true);
        if (pdbHint is not null)
            pdbHint.Visibility = width < 900 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void ApplyCompactModuleActions(MainWindow window, double width)
    {
        var compact = width < 820;
        var labels = new HashSet<string>(StringComparer.Ordinal)
        {
            "Auslastung aktualisieren",
            "Neue Zone planen",
            "Limits/Maps neu analysieren",
            "Neu bewerten",
            "Hook-Profil anwenden",
            "Letztes Backup",
            "Performance neu analysieren",
            "Fiesta.bin auswählen",
            "Client neu analysieren",
            "Vollanalyse",
            "Bericht exportieren",
            "Reparatur anwenden",
            "Monitor starten",
            "Monitor stoppen",
            "Timeline leeren",
            "PDBs indexieren",
            "Symbole suchen"
        };

        foreach (var button in ResponsiveDescendants<Button>(window))
        {
            var label = ResponsiveDescendants<TextBlock>(button)
                .FirstOrDefault(text => labels.Contains(text.Text ?? string.Empty));
            if (label is null)
                continue;

            button.ToolTip ??= label.Text;
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void EnsureTwoResponsiveRows(Grid grid)
    {
        if (grid.RowDefinitions.Count == 2)
        {
            grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            return;
        }

        grid.RowDefinitions.Clear();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    }
}
