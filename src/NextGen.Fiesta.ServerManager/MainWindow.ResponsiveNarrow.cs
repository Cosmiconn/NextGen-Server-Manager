using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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
                window.MinWidth = Math.Min(1024, Math.Max(640, workArea.Width - 24));
                window.MinHeight = Math.Min(680, Math.Max(520, workArea.Height - 24));
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
