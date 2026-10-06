using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<MainWindow, object> NarrowResponsiveWindows = new();
    private static readonly bool NarrowResponsiveHookRegistered = RegisterNarrowResponsiveHook();

    private static bool RegisterNarrowResponsiveHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnNarrowResponsiveLoaded));
        return true;
    }

    private static void OnNarrowResponsiveLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || NarrowResponsiveWindows.TryGetValue(window, out _))
            return;

        NarrowResponsiveWindows.Add(window, new object());
        window.SizeChanged += (_, _) => window.Dispatcher.BeginInvoke(
            new Action(() => ApplyNarrowResponsiveLayout(window)),
            DispatcherPriority.Background);

        window.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                var workArea = SystemParameters.WorkArea;
                window.MinWidth = Math.Max(640, Math.Min(720, workArea.Width - 24));
                window.MinHeight = Math.Max(520, Math.Min(620, workArea.Height - 24));
                FitResponsiveWindowToWorkArea(window, workArea);
                ApplyNarrowResponsiveLayout(window);
            }),
            DispatcherPriority.ContextIdle);
    }

    private static void ApplyNarrowResponsiveLayout(MainWindow window)
    {
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var main = window._mainNavigation;
        if (main is null || width <= 0)
            return;

        ApplyNarrowHeaderDensity(window, width);
        ApplyNarrowPrimaryHeaderDensity(main, width);

        // Above 960 DIP, MainWindow's reference-width calculation stays authoritative.
        // This narrow override exists only to remove the old 220-DIP floor when the
        // user deliberately resizes further down.
        if (width < 960)
        {
            var available = Math.Max(560, width - 32);
            var mainTabWidth = Math.Max(130, (available - 24) / 4.0);
            for (var i = 0; i < Math.Min(4, main.Items.Count); i++)
            {
                if (main.Items[i] is TabItem tab)
                    tab.Width = mainTabWidth;
            }
        }

        if (main.Items.Count > 1 && main.Items[1] is TabItem server && server.Content is TabControl serverSub)
            ResizeVisibleSubTabs(serverSub, width, 165, 270, 220, 215);

        if (main.Items.Count > 2 && main.Items[2] is TabItem diagnostic && diagnostic.Content is TabControl diagnosticSub)
            ResizeVisibleSubTabs(diagnosticSub, width, 155, 215, 190, 190);

        if (main.Items.Count > 3 && main.Items[3] is TabItem tools && tools.Content is TabControl toolsSub)
            ResizeVisibleSubTabs(toolsSub, width, 150, 220);
    }

    private static void ApplyNarrowHeaderDensity(MainWindow window, double width)
    {
        var headerGrid = ResponsiveDescendants<Grid>(window)
            .FirstOrDefault(candidate =>
                candidate.ColumnDefinitions.Count == 4 &&
                ResponsiveDescendants<TextBlock>(candidate)
                    .Any(text => text.Text == "NextGen Fiesta Server Manager"));
        if (headerGrid is null)
            return;

        var title = ResponsiveDescendants<TextBlock>(headerGrid)
            .FirstOrDefault(text => text.Text == "NextGen Fiesta Server Manager");
        var subtitle = ResponsiveDescendants<TextBlock>(headerGrid)
            .FirstOrDefault(text => text.Text?.StartsWith("NA2016 Diagnose", StringComparison.Ordinal) == true);
        var healthCard = headerGrid.Children.OfType<Border>()
            .FirstOrDefault(child => Grid.GetColumn(child) == 2);
        var healthTexts = healthCard is null
            ? new List<TextBlock>()
            : ResponsiveDescendants<TextBlock>(healthCard).ToList();
        var adminDetail = healthTexts.FirstOrDefault(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == "AdminText");

        if (width < 820)
        {
            headerGrid.ColumnDefinitions[0].Width = new GridLength(54);
            headerGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            headerGrid.ColumnDefinitions[2].Width = new GridLength(126);
            headerGrid.ColumnDefinitions[3].Width = new GridLength(62);
            if (title is not null)
                title.FontSize = width < 740 ? 17 : 19;
            if (subtitle is not null)
                subtitle.Visibility = Visibility.Collapsed;
            if (healthCard is not null)
            {
                healthCard.Width = 120;
                healthCard.Padding = new Thickness(9, 7, 8, 7);
            }
            if (adminDetail is not null)
                adminDetail.Visibility = Visibility.Collapsed;
        }
        else
        {
            headerGrid.ColumnDefinitions[0].Width = new GridLength(width < 1080 ? 64 : 66);
            headerGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            headerGrid.ColumnDefinitions[2].Width = new GridLength(width < 1080 ? 170 : 195);
            headerGrid.ColumnDefinitions[3].Width = new GridLength(width < 1080 ? 170 : 250);
            if (title is not null)
                title.FontSize = width < 1080 ? 22 : 25;
            if (subtitle is not null)
                subtitle.Visibility = Visibility.Visible;
            if (healthCard is not null)
            {
                healthCard.Width = width < 1080 ? 165 : 190;
                healthCard.Padding = new Thickness(13, 7, 12, 7);
            }
            if (adminDetail is not null)
                adminDetail.Visibility = Visibility.Visible;
        }
    }

    private static void ApplyNarrowPrimaryHeaderDensity(TabControl navigation, double width)
    {
        foreach (var tab in navigation.Items.OfType<TabItem>().Take(4))
        {
            if (tab.Header is not FrameworkElement header)
                continue;

            var textBlocks = ResponsiveDescendants<TextBlock>(header).ToList();
            var title = textBlocks
                .Where(text => text.FontWeight == FontWeights.SemiBold && text.FontSize >= 15)
                .OrderByDescending(text => text.FontSize)
                .FirstOrDefault();
            var subtitle = textBlocks
                .FirstOrDefault(text => text.FontSize <= 11 && text != title);
            var headerGrid = header as Grid ?? ResponsiveDescendants<Grid>(header).FirstOrDefault();

            if (width < 860)
            {
                if (headerGrid is not null && headerGrid.ColumnDefinitions.Count >= 2)
                    headerGrid.ColumnDefinitions[0].Width = new GridLength(width < 740 ? 30 : 38);
                if (title is not null)
                    title.FontSize = width < 740 ? 12 : 14;
                if (subtitle is not null)
                    subtitle.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (headerGrid is not null && headerGrid.ColumnDefinitions.Count >= 2)
                    headerGrid.ColumnDefinitions[0].Width = new GridLength(50);
                if (title is not null)
                    title.FontSize = 17;
                if (subtitle is not null)
                    subtitle.Visibility = Visibility.Visible;
            }
        }
    }

    private static void ResizeVisibleSubTabs(TabControl navigation, double windowWidth, double minWidth, params double[] referenceWidths)
    {
        if (referenceWidths.Length == 0)
            return;

        var visibleTabs = navigation.Items.OfType<TabItem>()
            .Where(item => item.Width > 0 && item.Opacity > 0)
            .Take(referenceWidths.Length)
            .ToList();
        if (visibleTabs.Count == 0)
            return;

        if (windowWidth >= 1180)
        {
            for (var i = 0; i < visibleTabs.Count; i++)
                visibleTabs[i].Width = referenceWidths[Math.Min(i, referenceWidths.Length - 1)];
            return;
        }

        var available = Math.Max(480, windowWidth - 64);
        var target = Math.Max(minWidth, (available - ((visibleTabs.Count - 1) * 6.0)) / visibleTabs.Count);
        foreach (var tab in visibleTabs)
            tab.Width = target;
    }
}
