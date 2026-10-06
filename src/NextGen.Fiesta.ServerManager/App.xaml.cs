using System.Text;
using System.Windows;
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
        }
        catch (Exception ex)
        {
            ReportFatal("STARTUP", ex);
            Shutdown(-1);
        }
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
