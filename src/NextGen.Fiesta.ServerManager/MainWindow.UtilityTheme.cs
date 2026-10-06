using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace NextGen.Fiesta.ServerManager;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<MainWindow, object> UtilityThemeWindows = new();
    private static readonly bool UtilityThemeHookRegistered = RegisterUtilityThemeHook();

    private static bool RegisterUtilityThemeHook()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnUtilityThemeLoaded));
        return true;
    }

    private static void OnUtilityThemeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || UtilityThemeWindows.TryGetValue(window, out _))
            return;

        UtilityThemeWindows.Add(window, new object());
        var text = (Brush)window.FindResource("Text");

        // The old Settings view used to inherit Foreground from its TabItem. Once the
        // same element is moved into the global utility overlay that inheritance chain
        // disappears and WPF falls back to black. Re-establish the dark-theme foreground
        // at the content host so the original controls remain untouched and functional.
        if (window._utilityContentHost is not null)
            window._utilityContentHost.Foreground = text;
        if (window._utilityTitle is not null)
            window._utilityTitle.Foreground = text;
    }
}
