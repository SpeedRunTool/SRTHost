using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SRTHost.ViewModels;
using SRTHost.Views;

namespace SRTHost;

/// <summary>
/// The application object: owns the window, the tray icon and the shutdown order.
/// </summary>
/// <remarks>
/// <see cref="ShutdownMode.OnExplicitShutdown"/> throughout, because this application's lifetime is
/// not its window's. The plugin runners are children of this process and are killed with it by the
/// job object, so a host that exited when the window closed would take a running overlay down with
/// it - the single most surprising thing a plugin host can do to somebody mid-run.
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class App : Application
{
    private readonly HostContext? host;

    private MainWindowViewModel? shell;

    /// <summary>Parameterless constructor for the XAML previewer, which builds the app with no host.</summary>
    public App()
    {
    }

    /// <summary>Creates the application over a constructed host.</summary>
    public App(HostContext host) => this.host = host;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && host is not null)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            shell = new MainWindowViewModel(host);

            MainWindow window = new() { DataContext = shell };

            window.CloseToTray = () => host.Settings.CloseToTray;

            desktop.MainWindow = window;

            if (!host.Settings.StartMinimized)
                window.Show();

            // The runtime is started after the window rather than before it, so plugin launches -
            // a process spawn and a handshake each - happen with the log view already on screen.
            _ = shell.StartAsync();

            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowWindow();

    private void OnShowClicked(object? sender, EventArgs e) => ShowWindow();

    private void OnPauseClicked(object? sender, EventArgs e) => shell?.PauseAllCommand.Execute(null);

    private void OnOpenPluginsClicked(object? sender, EventArgs e)
        => shell?.PluginsPage.OpenPluginsFolderCommand.Execute(null);

    private void OnExitClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void ShowWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            return;

        window.Show();

        // A window hidden while minimised comes back minimised, which looks exactly like the tray
        // click having done nothing.
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    /// <summary>
    /// Stops every runner before the process goes away.
    /// </summary>
    /// <remarks>
    /// Blocking, and deliberately so: this is the last chance to shut the runners down politely, and
    /// the job object's kill-on-close is the backstop rather than the plan. It is bounded so a wedged
    /// plugin cannot make Exit hang forever - the job object handles whatever is left.
    /// </remarks>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        shell?.Dispose();

        if (host is not null && !host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine(
                "Some plugin runners did not stop within ten seconds; the job object will terminate them.");
        }
    }
}
