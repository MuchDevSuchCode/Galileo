using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PhotosPlus.Services;

namespace PhotosPlus;

public partial class App : Application
{
    /// <summary>Process-wide persistent state (hidden/favorite flags, settings).</summary>
    public static AppState State { get; } = AppState.Load();

    /// <summary>Crash/error log path: %LocalAppData%\PhotosPlus\logs\error.log.</summary>
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotosPlus", "logs", "error.log");

    private Window? _window;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Log("UI", e.Exception);
            e.Handled = true; // keep the app alive so the error is logged and visible
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("Task", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(GetInitialMediaPath());
        _window.Activate();
    }

    /// <summary>Appends an exception (with stack trace) to the error log. Never throws.</summary>
    public static void Log(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static string? GetInitialMediaPath()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                if (Directory.Exists(arg)) return arg;
                if (File.Exists(arg) && PhotoLibrary.IsSupported(arg)) return arg;
            }
        }
        catch
        {
            // Ignore malformed arguments — fall back to a normal launch.
        }
        return null;
    }
}
