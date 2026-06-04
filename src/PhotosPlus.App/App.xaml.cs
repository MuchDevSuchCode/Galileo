using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
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

    /// <summary>Diagnostic trail of every thrown exception — the last entry before a hard crash
    /// (0xc000027b XAML failfast) is the real culprit, since those bypass the handlers above.</summary>
    public static string FirstChancePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotosPlus", "logs", "firstchance.log");

    private static readonly object _fcLock = new();

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

        // Capture every first-chance exception. WinUI render/dispatcher failfasts (0xc000027b)
        // skip the handlers above, but the underlying managed exception is still thrown first —
        // so the tail of this file pinpoints the crash. Best-effort; must never throw.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            try
            {
                var ex = e.Exception;
                var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";
                lock (_fcLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FirstChancePath)!);
                    File.AppendAllText(FirstChancePath, line);
                }
            }
            catch { /* diagnostics must never crash the app */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(GetInitialMediaPath());
        _window.Activate();
    }

    /// <summary>
    /// Called (on a background thread) by the single-instance host when another launch is redirected
    /// here. Opens the handed-off file/folder in the existing window and brings it forward.
    /// </summary>
    public void OnRedirected(AppActivationArguments e)
    {
        var path = PathFromActivation(e);
        var window = _window;
        if (window is null) return;
        window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (window is MainWindow mw && !string.IsNullOrEmpty(path)) mw.OpenExternalPath(path!);
                window.Activate();
            }
            catch (Exception ex) { Log("Redirected", ex); }
        });
    }

    private static string? PathFromActivation(AppActivationArguments e)
    {
        try
        {
            if (e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fa && fa.Files.Count > 0)
                return fa.Files[0].Path;
            if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la)
                return FirstExistingPath(SplitArgs(la.Arguments));
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? FirstExistingPath(IEnumerable<string> args)
    {
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (File.Exists(a) || Directory.Exists(a)) return a;
        }
        return null;
    }

    private static IEnumerable<string> SplitArgs(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) yield break;
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ' ' && !inQuotes)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
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
