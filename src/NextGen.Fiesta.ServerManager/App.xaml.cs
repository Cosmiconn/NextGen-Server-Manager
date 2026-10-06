using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NextGen.Fiesta.ServerManager;

public partial class App : System.Windows.Application
{
    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NextGenFiestaServerManager");

    public static string StartupLogPath => Path.Combine(LogDirectory, "startup.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.WriteAllText(StartupLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] START NextGen Fiesta Server Manager {GetType().Assembly.GetName().Version}\r\n" +
                $"BaseDirectory={AppContext.BaseDirectory}\r\n" +
                $"ProcessPath={Environment.ProcessPath}\r\n" +
                $"OS={Environment.OSVersion}\r\n" +
                $"64BitProcess={Environment.Is64BitProcess}\r\n\r\n",
                Encoding.UTF8);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            Log("MainWindow successfully shown.");

            if (string.Equals(
                    Environment.GetEnvironmentVariable("NEXTGEN_UI_SMOKE_RESIZE"),
                    "1",
                    StringComparison.Ordinal))
            {
                _ = RunUiResizeSmokeAsync(window);
            }
        }
        catch (Exception ex)
        {
            ReportFatal("STARTUP", ex);
            Shutdown(-1);
        }
    }

    private static async Task RunUiResizeSmokeAsync(MainWindow window)
    {
        try
        {
            var originalWidth = window.Width;
            var originalHeight = window.Height;
            var targets = new (double Width, double Height)[]
            {
                (1180, 760),
                (900, 700),
                (720, 620),
                (1280, 800)
            };

            foreach (var target in targets)
            {
                window.WindowState = WindowState.Normal;
                window.Width = Math.Max(window.MinWidth, target.Width);
                window.Height = Math.Max(window.MinHeight, target.Height);
                await Task.Delay(450);
                window.UpdateLayout();
                Log(
                    $"UI_SMOKE_RESIZE target={target.Width:F0}x{target.Height:F0} " +
                    $"actual={window.ActualWidth:F0}x{window.ActualHeight:F0} " +
                    $"min={window.MinWidth:F0}x{window.MinHeight:F0}");
                CaptureUiSmokeScreenshot(window, target.Width, target.Height);
            }

            // Exercise every migrated view on a realistic compact desktop size. This
            // catches selection-time layout/resource failures that startup alone misses.
            window.Width = Math.Max(window.MinWidth, 900);
            window.Height = Math.Max(window.MinHeight, 700);
            await Task.Delay(350);
            await RunUiNavigationSmokeAsync(window);

            window.Width = Math.Max(window.MinWidth, originalWidth);
            window.Height = Math.Max(window.MinHeight, originalHeight);
            await Task.Delay(450);
            Log("UI_SMOKE_RESIZE completed.");
        }
        catch (Exception ex)
        {
            ReportFatal("UI_SMOKE_RESIZE", ex);
            Current.Shutdown(-3);
        }
    }

    private static async Task RunUiNavigationSmokeAsync(MainWindow window)
    {
        var main = window.UiSmokeMainNavigation
            ?? throw new InvalidOperationException("Main navigation was not created.");
        if (main.Items.Count < 4)
            throw new InvalidOperationException($"Expected four main navigation items, found {main.Items.Count}.");

        async Task SelectMainAsync(int index, string name)
        {
            main.SelectedIndex = index;
            await Task.Delay(250);
            window.UpdateLayout();
            CaptureUiSmokeScreenshot(window, 900, 700, $"nav-{name}");
            Log($"UI_SMOKE_NAV main={name}");
        }

        async Task SelectSubAsync(int mainIndex, int subIndex, string name)
        {
            main.SelectedIndex = mainIndex;
            await Task.Delay(120);
            if (main.Items[mainIndex] is not TabItem mainTab || mainTab.Content is not TabControl sub)
                throw new InvalidOperationException($"Navigation '{name}' does not expose a sub-navigation TabControl.");
            if (subIndex < 0 || subIndex >= sub.Items.Count)
                throw new InvalidOperationException($"Navigation '{name}' sub-index {subIndex} is out of range ({sub.Items.Count}).");

            sub.SelectedIndex = subIndex;
            await Task.Delay(250);
            window.UpdateLayout();
            CaptureUiSmokeScreenshot(window, 900, 700, $"nav-{name}");
            Log($"UI_SMOKE_NAV view={name}");
        }

        await SelectMainAsync(0, "dashboard");

        await SelectSubAsync(1, 0, "server-zone-capacity");
        await SelectSubAsync(1, 1, "server-limits");
        await SelectSubAsync(1, 2, "server-adaptive-hooks");
        await SelectSubAsync(1, 3, "server-performance-overflow");

        await SelectSubAsync(2, 0, "diagnostic-logs");
        await SelectSubAsync(2, 1, "diagnostic-timeline");
        await SelectSubAsync(2, 2, "diagnostic-pdb");

        await SelectSubAsync(3, 0, "tools-client-map-safety");

        // Return to the reference start page for the final frame/state.
        main.SelectedIndex = 1;
        if (main.Items[1] is TabItem serverTab && serverTab.Content is TabControl serverSub)
            serverSub.SelectedIndex = 0;
        await Task.Delay(200);
        window.UpdateLayout();
        Log("UI_SMOKE_NAV completed.");
    }

    private static void CaptureUiSmokeScreenshot(
        MainWindow window,
        double targetWidth,
        double targetHeight,
        string? label = null)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("NEXTGEN_UI_SMOKE_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return;

        Directory.CreateDirectory(outputDirectory);
        window.UpdateLayout();

        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var prefix = string.IsNullOrWhiteSpace(label)
            ? $"ui-target-{targetWidth:F0}x{targetHeight:F0}"
            : $"ui-{label}";
        var fileName = $"{prefix}-actual-{width}x{height}.png";
        var path = Path.Combine(outputDirectory, fileName);
        using (var stream = File.Create(path))
            encoder.Save(stream);

        Log($"UI_SMOKE_SCREENSHOT {fileName}");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatal("DISPATCHER", e.Exception);
        e.Handled = true;
        Shutdown(-2);
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) ReportFatal("APPDOMAIN", ex);
        else Log($"APPDOMAIN fatal object: {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("UNOBSERVED TASK: " + e.Exception);
        e.SetObserved();
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(StartupLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n",
                Encoding.UTF8);
        }
        catch { }
    }

    private static void ReportFatal(string stage, Exception ex)
    {
        Log($"FATAL {stage}: {ex}");
        try
        {
            MessageBox.Show(
                $"NextGen Fiesta Server Manager konnte nicht starten.\n\n" +
                $"Phase: {stage}\n" +
                $"Fehler: {ex.GetType().FullName}\n" +
                $"Meldung: {ex.Message}\n\n" +
                $"Diagnoselog:\n{StartupLogPath}",
                "NextGen Fiesta Server Manager – Startfehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
    }
}
