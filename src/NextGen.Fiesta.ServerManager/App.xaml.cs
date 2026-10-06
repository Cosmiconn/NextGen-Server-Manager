using System.Text;
using System.Windows;
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

            // CI can opt into a real dispatcher-driven resize smoke without changing
            // normal production startup. This exercises the SizeChanged breakpoints,
            // visual-tree transformations and narrow layouts that a compile cannot test.
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

                // Give WPF rendering, SizeChanged handlers and queued responsive passes
                // enough dispatcher turns to settle before inspecting/capturing the frame.
                await Task.Delay(450);
                window.UpdateLayout();
                Log(
                    $"UI_SMOKE_RESIZE target={target.Width:F0}x{target.Height:F0} " +
                    $"actual={window.ActualWidth:F0}x{window.ActualHeight:F0} " +
                    $"min={window.MinWidth:F0}x{window.MinHeight:F0}");
                CaptureUiSmokeScreenshot(window, target.Width, target.Height);
            }

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

    private static void CaptureUiSmokeScreenshot(MainWindow window, double targetWidth, double targetHeight)
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
        var fileName = $"ui-target-{targetWidth:F0}x{targetHeight:F0}-actual-{width}x{height}.png";
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
