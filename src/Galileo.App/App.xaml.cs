using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Galileo.Services;

namespace Galileo;

public partial class App : Application
{
    /// <summary>Process-wide persistent state (hidden/favorite flags, settings).</summary>
    public static AppState State { get; } = AppState.Load();

    /// <summary>Process-wide vault manager. One instance for ALL windows: a per-window manager let a
    /// guest photo window unlock a vault the primary already had open, which re-decrypted (wiping) the
    /// live working folder, and let the process exit thinking no vault was unlocked.</summary>
    public static VaultManager Vaults { get; } = new();

    /// <summary>Crash/error log path: &lt;data root&gt;\logs\error.log.</summary>
    public static string LogPath { get; } = Path.Combine(AppPaths.Root, "logs", "error.log");

    /// <summary>Diagnostic trail of every thrown exception — the last entry before a hard crash
    /// (0xc000027b XAML failfast) is the real culprit, since those bypass the handlers above.</summary>
    public static string FirstChancePath { get; } = Path.Combine(AppPaths.Root, "logs", "firstchance.log");

    private static readonly object _fcLock = new();

    /// <summary>True once this process is registered as the single-instance key holder and its Activated
    /// handler is wired to <see cref="OnRedirected"/> (by Program.Main or, mid-session, by the tray).</summary>
    internal static bool SingleInstanceHooked;

    // Held for the whole process lifetime; lets a later launch tell "another Galileo is alive right
    // now" apart from "a previous Galileo crashed" — startup cleanup must only run in the second case,
    // or a second ordinary launch would wipe the first instance's live vault working folder and
    // archive/device temp files out from under it (they share %LocalAppData%\Galileo).
    private static System.Threading.Mutex? _aliveMutex;
    private static bool _aliveOwned;

    /// <summary>True when this process is the only running Galileo (it now owns the liveness mutex) —
    /// the only situation in which crash-recovery cleanup of shared temp/work folders is safe.</summary>
    public static bool IsOnlyInstance
    {
        get
        {
            if (_aliveMutex is null)
            {
                try
                {
                    _aliveMutex = new System.Threading.Mutex(initiallyOwned: true, "Galileo.ProcessAlive", out _aliveOwned);
                    if (!_aliveOwned)
                    {
                        // Not first — but the holder may have died; a short wait claims an abandoned mutex.
                        try { _aliveOwned = _aliveMutex.WaitOne(0); }
                        catch (System.Threading.AbandonedMutexException) { _aliveOwned = true; }
                    }
                }
                catch { _aliveOwned = false; }
            }
            return _aliveOwned;
        }
    }

    private Window? _window;

    /// <summary>The single reusable media-viewer window used by "always open media in a new window".
    /// Successive media opens load into THIS window instead of each spawning its own — one window, one
    /// MediaPlayer, so video decode sessions can't pile up (they used to, until the GPU ran out and
    /// playback went black). Explicit "Open in new window" (Alt+click / context menu) still makes its own
    /// separate windows. Set by MainWindow when it creates/reuses the viewer; cleared when it closes.</summary>
    internal MainWindow? MediaViewer { get; set; }

    public App()
    {
        InitializeComponent();

        _ = IsOnlyInstance; // claim the liveness mutex up front, before any window defers file-manager init

        UnhandledException += (_, e) =>
        {
            Log("UI", e.Exception);
            e.Handled = true; // keep the app alive so the error is logged and visible
            // "Handled" must not mean "invisible": tell the user something failed instead of the app
            // silently carrying on in a possibly inconsistent state.
            TryReportError(e.Exception);
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
                var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {ex.GetType().FullName} (0x{ex.HResult:X8}): {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";
                lock (_fcLock) AppendCapped(FirstChancePath, line);
            }
            catch { /* diagnostics must never crash the app */ }
        };
    }

    /// <summary>Best-effort user-visible notice for an unexpected error (status bar of the main
    /// window). Never throws; falls back to log-only when no window is up.</summary>
    private void TryReportError(Exception? ex)
    {
        try
        {
            if (_window is not MainWindow mw) return;
            var message = ex?.Message is { Length: > 0 } m ? m : "an unexpected error occurred";
            mw.DispatcherQueue.TryEnqueue(() =>
            {
                try { mw.ReportBackgroundError($"Something went wrong: {message} (details in the error log)"); }
                catch { }
            });
        }
        catch { /* error reporting must never crash the app */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LogInfo($"OnLaunched args=[{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}]");
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
        var newWindow = WantsNewWindow(e);
        LogInfo($"OnRedirected kind={e.Kind} newWindow={newWindow} path={path ?? "(none)"}");
        var window = _window;
        if (window is null) return;
        window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (newWindow)
                {
                    // "Open in new window": an additional in-process window, instantly — instead of a
                    // separate process paying a full cold start of the self-contained app each time.
                    // secondaryWindow: it's a guest of this process — no tray icon, no crash recovery.
                    var extra = new MainWindow(path, secondaryWindow: true);
                    extra.Activate();
                    return;
                }
                if (window is MainWindow mw)
                {
                    if (!string.IsNullOrEmpty(path)) mw.OpenExternalPath(path!);
                    mw.RestoreFromBackground(); // un-hide if it was minimized to the tray, then bring to front
                }
                else window.Activate();
            }
            catch (Exception ex) { Log("Redirected", ex); }
        });
    }

    /// <summary>True when the redirected launch asked for its own window (`--new-window`).</summary>
    private static bool WantsNewWindow(AppActivationArguments e)
    {
        try
        {
            if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la)
                return la.Arguments.Contains("--new-window", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* ignore */ }
        return false;
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
        // Only ever open a real folder or media file the user passed — never a flag (e.g. --background) and
        // never our own executable. The activation command line always starts with the exe path; opening it
        // navigated to the app folder and then "opened" the exe, relaunching Galileo in a fork-bomb loop.
        var self = Environment.ProcessPath;
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith('-')) continue;
            if (!string.IsNullOrEmpty(self) && string.Equals(a, self, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(a)) return a;
            if (File.Exists(a) && (PhotoLibrary.IsSupported(a) || PhotoLibrary.IsMedia(a))) return a;
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

    // Logs must stay bounded: repeated I/O errors during a long session (or the first-chance trail)
    // otherwise grow without limit. When a log passes the cap, the current file rotates to *.old
    // (replacing the previous .old) so recent history survives while total size stays ~2×cap.
    private const long LogCapBytes = 5 * 1024 * 1024;

    private static void AppendCapped(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > LogCapBytes)
            {
                var old = path + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
        }
        catch { /* rotation is best-effort */ }
        File.AppendAllText(path, text);
    }

    /// <summary>Appends an exception (with stack trace) to the error log. Never throws.</summary>
    public static void Log(string source, Exception? ex)
    {
        try
        {
            AppendCapped(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    /// <summary>Diagnostic info log: %LocalAppData%\Galileo\logs\app.log (lifecycle, sharing, tray — not errors).</summary>
    public static readonly string InfoLogPath = Path.Combine(AppPaths.Root, "logs", "app.log");

    public static void LogInfo(string message)
    {
        try
        {
            AppendCapped(InfoLogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }

    private static string? GetInitialMediaPath()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                if (Directory.Exists(arg)) return arg;
                if (File.Exists(arg) && (PhotoLibrary.IsSupported(arg) || PhotoLibrary.IsMedia(arg))) return arg;
            }
        }
        catch
        {
            // Ignore malformed arguments — fall back to a normal launch.
        }
        return null;
    }
}
