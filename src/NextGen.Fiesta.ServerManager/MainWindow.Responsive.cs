using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<MainWindow, object> ResponsiveWindows = new();

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnResponsiveWindowLoaded));
    }

    private static void OnResponsiveWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || ResponsiveWindows.TryGetValue(window, out _))
        {
            return;
        }

        ResponsiveWindows.Add(window, new object());

        // 1536x864 stays the visual reference size, but it must never become a fixed
        // canvas. Allow the user to reach the narrow layout breakpoints on a normal
        // desktop while still enforcing a practical floor for the operations UI.
        var workArea = SystemParameters.WorkArea;
        window.MinWidth = Math.Max(640, Math.Min(720, workArea.Width - 24));
        window.MinHeight = Math.Max(520, Math.Min(620, workArea.Height - 24));
        FitResponsiveWindowToWorkArea(window, workArea);

        window.SizeChanged += (_, _) => ApplyResponsiveLayout(window);
        window.StateChanged += (_, _) => window.Dispatcher.BeginInvoke(
            new Action(() => ApplyResponsiveLayout(window)),
            DispatcherPriority.Loaded);

        // Class handlers run before normal Loaded handlers. Run once more afterwards so
        // the responsive pass wins over the reference-width initialization when needed.
        window.Dispatcher.BeginInvoke(
            new Action(() => ApplyResponsiveLayout(window)),
            DispatcherPriority.Loaded);
    }

    private static void FitResponsiveWindowToWorkArea(MainWindow window, Rect workArea)
    {
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        var maxWidth = Math.Max(window.MinWidth, workArea.Width - 16);
        var maxHeight = Math.Max(window.MinHeight, workArea.Height - 16);
        if (window.Width > maxWidth)
        {
            window.Width = maxWidth;
        }
        if (window.Height > maxHeight)
        {
            window.Height = maxHeight;
        }

        if (double.IsNaN(window.Left) || window.Left < workArea.Left || window.Left + window.Width > workArea.Right)
        {
            window.Left = workArea.Left + Math.Max(0, (workArea.Width - window.Width) / 2.0);
        }
        if (double.IsNaN(window.Top) || window.Top < workArea.Top || window.Top + window.Height > workArea.Bottom)
        {
            window.Top = workArea.Top + Math.Max(0, (workArea.Height - window.Height) / 2.0);
        }
    }

    private static void ApplyResponsiveLayout(MainWindow window)
    {
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        if (width <= 0)
        {
            return;
        }

        ApplyResponsiveHeader(window, width);
        ApplyResponsiveToolbar(window, width);
        ApplyResponsiveCapacityCards(window, width);
        ApplyResponsiveProvisioning(window, width);
        ApplyResponsiveCapacityTable(window);
        ApplyResponsivePrimaryNavigation(window, width);
    }

    private static void ApplyResponsiveHeader(MainWindow window, double width)
    {
        var grid = ResponsiveDescendants<Grid>(window)
            .FirstOrDefault(candidate =>
                candidate.ColumnDefinitions.Count == 4 &&
                ResponsiveDescendants<TextBlock>(candidate).Any(text => text.Text == "NextGen Fiesta Server Manager"));
        if (grid is null)
        {
            return;
        }

        var compact = width < 1080;
        grid.ColumnDefinitions[0].Width = new GridLength(compact ? 64 : 66);
        grid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[2].Width = new GridLength(compact ? 170 : 195);
        grid.ColumnDefinitions[3].Width = new GridLength(compact ? 170 : 250);

        var title = ResponsiveDescendants<TextBlock>(grid).FirstOrDefault(text => text.Text == "NextGen Fiesta Server Manager");
        if (title is not null)
        {
            title.FontSize = compact ? 22 : 25;
            title.TextWrapping = TextWrapping.NoWrap;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        var subtitle = ResponsiveDescendants<TextBlock>(grid)
            .FirstOrDefault(text => text.Text?.StartsWith("NA2016 Diagnose", StringComparison.Ordinal) == true);
        if (subtitle is not null)
        {
            subtitle.FontSize = compact ? 11 : 14;
            subtitle.TextWrapping = TextWrapping.NoWrap;
            subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        var health = grid.Children
            .OfType<Border>()
            .FirstOrDefault(child => Grid.GetColumn(child) == 2);
        if (health is not null)
        {
            health.Width = compact ? 165 : 190;
        }

        var elevated = grid.Children
            .OfType<Button>()
            .FirstOrDefault(button => ResponsiveContainsText(button, "Als Administrator neu starten"));
        if (elevated is not null)
        {
            elevated.Width = compact ? 52 : 230;
            elevated.ToolTip = "Als Administrator neu starten";
            SetResponsiveButtonLabelVisible(elevated, "Als Administrator neu starten", !compact);
        }
    }

    private static void ApplyResponsiveToolbar(MainWindow window, double width)
    {
        var toolbar = ResponsiveDescendants<Grid>(window)
            .FirstOrDefault(candidate =>
                candidate.ColumnDefinitions.Count == 10 &&
                candidate.Children.OfType<Button>().Any(button => ResponsiveContainsText(button, "Scannen")) &&
                candidate.Children.OfType<Button>().Any(button => ResponsiveContainsText(button, "Status aktualisieren")));
        if (toolbar is null)
        {
            return;
        }

        var compact = width < 1160;
        if (compact)
        {
            SetResponsiveColumnWidths(toolbar,
                new GridLength(50),
                new GridLength(1, GridUnitType.Star),
                new GridLength(0),
                new GridLength(46),
                new GridLength(46),
                new GridLength(52),
                new GridLength(8),
                new GridLength(46),
                new GridLength(46),
                new GridLength(46));
        }
        else
        {
            SetResponsiveColumnWidths(toolbar,
                new GridLength(136),
                new GridLength(1, GridUnitType.Star),
                new GridLength(46),
                new GridLength(112),
                new GridLength(105),
                new GridLength(178),
                new GridLength(12),
                new GridLength(112),
                new GridLength(100),
                new GridLength(72));
        }

        var rootLabel = ResponsiveDescendants<TextBlock>(toolbar).FirstOrDefault(text => text.Text == "Server Root");
        if (rootLabel is not null)
        {
            rootLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }

        var browseIcon = toolbar.Children
            .OfType<Button>()
            .FirstOrDefault(button => Grid.GetColumn(button) == 2);
        if (browseIcon is not null)
        {
            browseIcon.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }

        SetResponsiveToolbarButton(toolbar, "Ordner…", compact);
        SetResponsiveToolbarButton(toolbar, "Scannen", compact);
        SetResponsiveToolbarButton(toolbar, "Status aktualisieren", compact);
        SetResponsiveToolbarButton(toolbar, "Einstellungen", compact);
        SetResponsiveToolbarButton(toolbar, "Handbuch", compact);
        SetResponsiveToolbarButton(toolbar, "Credits", compact);
    }

    private static void SetResponsiveToolbarButton(Grid toolbar, string label, bool compact)
    {
        var button = toolbar.Children
            .OfType<Button>()
            .FirstOrDefault(candidate => ResponsiveContainsText(candidate, label));
        if (button is null)
        {
            return;
        }

        button.ToolTip = label;
        SetResponsiveButtonLabelVisible(button, label, !compact);
    }

    private static void ApplyResponsiveCapacityCards(MainWindow window, double width)
    {
        var cards = ResponsiveDescendants<UniformGrid>(window)
            .FirstOrDefault(candidate =>
                candidate.Children.Count == 6 &&
                ResponsiveDescendants<TextBlock>(candidate).Any(text => text.Text == "Laufende Zonen") &&
                ResponsiveDescendants<TextBlock>(candidate).Any(text => text.Text == "Höchste Zonen-Auslastung"));
        if (cards is null)
        {
            return;
        }

        var columns = width >= 1180 ? 6 : width >= 760 ? 3 : 2;
        cards.Columns = columns;
        cards.Rows = 0;

        foreach (var card in cards.Children.OfType<Border>())
        {
            card.Height = 66;
            card.Margin = columns == 6
                ? new Thickness(0, 0, 7, 0)
                : new Thickness(0, 0, 7, 7);
        }
    }

    private static void ApplyResponsiveProvisioning(MainWindow window, double width)
    {
        var fields = ResponsiveDescendants<Grid>(window)
            .FirstOrDefault(candidate =>
                candidate.ColumnDefinitions.Count == 7 &&
                ResponsiveDescendants<TextBlock>(candidate).Any(text => text.Text == "Neue Zone") &&
                ResponsiveDescendants<TextBlock>(candidate).Any(text => text.Text == "Template") &&
                ResponsiveDescendants<TextBlock>(candidate).Any(text => text.Text == "Ports") &&
                ResponsiveDescendants<TextBlock>(candidate).Any(text => text.Text == "Bereit"));
        if (fields is null)
        {
            return;
        }

        var compact = width < 1360;
        if (compact)
        {
            var createWidth = width < 980 ? 52 : 160;
            SetResponsiveColumnWidths(fields,
                new GridLength(1.0, GridUnitType.Star),
                new GridLength(1.3, GridUnitType.Star),
                new GridLength(1.35, GridUnitType.Star),
                new GridLength(0.9, GridUnitType.Star),
                new GridLength(1.8, GridUnitType.Star),
                new GridLength(44),
                new GridLength(createWidth));
        }
        else
        {
            SetResponsiveColumnWidths(fields,
                new GridLength(125),
                new GridLength(290),
                new GridLength(300),
                new GridLength(170),
                new GridLength(1, GridUnitType.Star),
                new GridLength(44),
                new GridLength(180));
        }

        var create = fields.Children
            .OfType<Button>()
            .FirstOrDefault(button => ResponsiveContainsText(button, "Zone jetzt anlegen"));
        if (create is not null)
        {
            var iconOnly = width < 980;
            create.ToolTip = "Zone jetzt anlegen";
            SetResponsiveButtonLabelVisible(create, "Zone jetzt anlegen", !iconOnly);
        }
    }

    private static void ApplyResponsiveCapacityTable(MainWindow window)
    {
        var table = ResponsiveDescendants<DataGrid>(window)
            .FirstOrDefault(candidate =>
                candidate.Columns.Count >= 10 &&
                candidate.Columns.Any(column => string.Equals(column.Header?.ToString(), "Zone", StringComparison.Ordinal)) &&
                candidate.Columns.Any(column => string.Equals(column.Header?.ToString(), "Maps / BlockInfo", StringComparison.Ordinal)));
        if (table is null)
        {
            return;
        }

        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto);
    }

    private static void ApplyResponsivePrimaryNavigation(MainWindow window, double width)
    {
        // The exact reference calculation in MainWindow.xaml.cs remains authoritative
        // at normal desktop widths. Only remove its legacy floor when the user deliberately
        // resizes into the narrow operating mode.
        if (width >= 960)
        {
            return;
        }

        var navigation = ResponsiveDescendants<TabControl>(window)
            .FirstOrDefault(candidate =>
                candidate.Items.Count >= 4 &&
                candidate.Items.OfType<TabItem>().Any(tab => ResponsiveHeaderContains(tab, "Dashboard")) &&
                candidate.Items.OfType<TabItem>().Any(tab => ResponsiveHeaderContains(tab, "Serverleistung")));
        if (navigation is null)
        {
            return;
        }

        var available = Math.Max(560, width - 32);
        var tabWidth = Math.Max(130, (available - 24) / 4.0);
        foreach (var tab in navigation.Items.OfType<TabItem>().Take(4))
        {
            tab.Width = tabWidth;
        }
    }

    private static bool ResponsiveHeaderContains(TabItem tab, string value)
    {
        if (tab.Header is string text)
        {
            return text.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        return tab.Header is DependencyObject header &&
               ResponsiveDescendants<TextBlock>(header).Any(text =>
                   text.Text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool ResponsiveContainsText(DependencyObject root, string value)
        => ResponsiveDescendants<TextBlock>(root).Any(text => string.Equals(text.Text, value, StringComparison.Ordinal));

    private static void SetResponsiveButtonLabelVisible(Button button, string label, bool visible)
    {
        var text = ResponsiveDescendants<TextBlock>(button)
            .FirstOrDefault(candidate => string.Equals(candidate.Text, label, StringComparison.Ordinal));
        if (text is not null)
        {
            text.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetResponsiveColumnWidths(Grid grid, params GridLength[] widths)
    {
        for (var i = 0; i < widths.Length && i < grid.ColumnDefinitions.Count; i++)
        {
            grid.ColumnDefinitions[i].Width = widths[i];
        }
    }

    private static IEnumerable<T> ResponsiveDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in ResponsiveDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
