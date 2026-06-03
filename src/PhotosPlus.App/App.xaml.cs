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
        _window = new MainWindow();
        _window.Activate();
    }
}
