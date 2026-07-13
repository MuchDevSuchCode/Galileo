using System;
using System.Linq;
using Galileo.Services;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Microsoft.Windows.AppLifecycle;

namespace Galileo;

public sealed partial class MainWindow
{
    // ===================== Run in background (system tray + autostart) =====================
    // A true Windows Service can't host a WinUI window/tray (session 0, no desktop), so "run in the
    // background" is the standard tray-app pattern: closing the window hides it to the notification area
    // and keeps the process (and secure-sharing host) alive; quit from the tray menu. Optional autostart
    // launches it minimized to the tray at sign-in.

    private TrayIcon? _tray;
    private bool _exitingFromTray;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Galileo";

    private static bool HasArg(string flag) =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static bool LaunchedInBackground() => HasArg("--background");

    /// <summary>A transient "open in its own window" instance — never a tray-resident background host.</summary>
    private static bool LaunchedNewWindow() => HasArg("--new-window");

    /// <summary>Wire up tray + background behavior at startup from saved settings.</summary>
    private void InitBackground()
    {
        // An "open in new window" window is a throwaway viewer belonging to the primary — it must NOT create
        // a tray icon or run in the background, or every opened photo leaves a permanent tray copy.
        // _secondaryWindow, not just LaunchedNewWindow(): these windows are created in-process by the
        // primary now, so the command line is the primary's and doesn't mention --new-window.
        if (_secondaryWindow || LaunchedNewWindow()) return;

        if (_state.RunInBackground || _state.StartWithWindows) EnsureTray();
        if (LaunchedInBackground() && _tray is not null)
            DispatcherQueue.TryEnqueue(() => { try { _appWindow.Hide(); } catch { } });
    }

    private void EnsureTray()
    {
        EnsureSingleInstanceRegistered(); // so clicking the app icon reuses this tray instance
        if (_tray is not null) return;
        try
        {
            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "galileo.ico");
            _tray = new TrayIcon("Galileo", icon);
            _tray.OpenRequested += () => DispatcherQueue.TryEnqueue(ShowFromTray);
            _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitFromTray);
        }
        catch (Exception ex) { App.Log("Tray", ex); }
    }

    // Claim the single-instance key now (if Program.Main didn't, e.g. background was enabled mid-session)
    // so subsequent launches redirect here instead of starting a second copy.
    private void EnsureSingleInstanceRegistered()
    {
        if (App.SingleInstanceHooked) return;
        try
        {
            var key = AppInstance.FindOrRegisterForKey("Galileo-SingleInstance");
            if (key.IsCurrent)
            {
                key.Activated += (_, e) => (Application.Current as App)?.OnRedirected(e);
                App.SingleInstanceHooked = true;
            }
        }
        catch (Exception ex) { App.Log("SingleInstance", ex); }
    }

    private void RemoveTray()
    {
        try { _tray?.Dispose(); } catch { }
        _tray = null;
    }

    /// <summary>Bring the window back from the tray (used by tray Open and single-instance re-activation).</summary>
    public void RestoreFromBackground() => ShowFromTray();

    private void ShowFromTray()
    {
        try
        {
            _appWindow.Show();
            Activate();
            _appWindow.MoveInZOrderAtTop();
        }
        catch (Exception ex) { App.Log("TrayShow", ex); }
    }

    private void ExitFromTray()
    {
        _exitingFromTray = true;
        Close(); // routes through AppWindow_Closing → full cleanup (lock vault, wipe temp) → exit
    }

    // ---- settings handlers ----

    private void RunInBackgroundSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.RunInBackground = RunInBackgroundSwitch.IsOn;
        _state.Save();
        if (_state.RunInBackground) EnsureTray();
        else if (!_state.StartWithWindows) RemoveTray();
    }

    private void StartWithWindowsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.StartWithWindows = StartWithWindowsSwitch.IsOn;
        // Autostarting only makes sense if it stays resident — keep the two in step.
        if (_state.StartWithWindows && !_state.RunInBackground)
        {
            _state.RunInBackground = true;
            RunInBackgroundSwitch.IsOn = true;
        }
        _state.Save();
        SetStartWithWindows(_state.StartWithWindows);
        if (_state.StartWithWindows || _state.RunInBackground) EnsureTray();
        else RemoveTray();
    }

    private static void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValueName, $"\"{exe}\" --background");
            }
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { App.Log("Autostart", ex); }
    }
}
