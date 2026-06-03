using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using PhotosPlus.Services;

namespace PhotosPlus;

public partial class App : Application
{
    /// <summary>Process-wide persistent state (hidden/favorite flags, settings).</summary>
    public static AppState State { get; } = AppState.Load();

    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // When set as the default photo app, Windows launches us as `PhotosPlus.App.exe "<file>"`.
        _window = new MainWindow(GetInitialMediaPath());
        _window.Activate();
    }

    /// <summary>
    /// Returns a folder or supported image path passed on the command line (file association
    /// or "Open with"), or null for a normal launch.
    /// </summary>
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
