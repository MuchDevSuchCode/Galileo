using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Galileo;

/// <summary>
/// Custom entry point (replaces the auto-generated Main via DISABLE_XAML_GENERATED_MAIN) so that,
/// when single-instance mode is enabled, activation redirection happens before XAML initializes.
/// Doing it here — rather than in OnLaunched — is the supported, crash-free pattern.
/// </summary>
public static class Program
{
    private static App? _app;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection()) return 0; // handed off to the primary instance — exit quietly

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _app = new App();
        });
        return 0;
    }

    /// <summary>
    /// Returns true if this launch was redirected to an already-running primary instance.
    /// Only engages when the user enabled single-instance mode; otherwise normal multi-window.
    /// </summary>
    private static bool DecideRedirection()
    {
        if (!App.State.SingleInstance) return false;
        try
        {
            var keyInstance = AppInstance.FindOrRegisterForKey("Galileo-SingleInstance");
            if (keyInstance.IsCurrent)
            {
                keyInstance.Activated += (_, e) => _app?.OnRedirected(e);
                return false;
            }
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            keyInstance.RedirectActivationToAsync(activation).AsTask().Wait();
            return true;
        }
        catch
        {
            return false; // any failure → fall back to a normal launch
        }
    }
}
