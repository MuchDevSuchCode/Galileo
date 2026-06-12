using System;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>
/// Runs a delegate on a dedicated STA thread and awaits the result. Used for Windows shell-namespace
/// COM work (MTP/portable-device enumeration) so it stays in a single-threaded apartment but off the
/// UI thread — a slow or stalled device no longer freezes the window.
/// </summary>
public static class StaTask
{
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "Galileo-STA-shell" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
