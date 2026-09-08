using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SRTHost.Core.Routing;
using SRTHost.Core.Supervision;
using SRTPluginBase.Abstractions;

namespace SRTHost.ViewModels;

/// <summary>
/// The shell: a nav rail, the page it is showing, and the status bar under both.
/// </summary>
/// <remarks>
/// It owns the pages and tells them when they become visible, which is what lets the data inspector
/// attach its tap on the router only while it is on screen.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly HostContext host;
    private readonly ILogger logger;
    private readonly DispatcherTimer status;

    /// <summary>The pages the nav rail lists, in order.</summary>
    public ObservableCollection<ViewModelBase> Pages { get; }

    /// <summary>The plugin list, which the tray menu and the startup sequence both need by name.</summary>
    public PluginsViewModel PluginsPage { get; }

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    /// <summary>The one-line summary in the status bar.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Starting…";

    /// <summary>Measured producer-to-consumer latency, for the status bar.</summary>
    [ObservableProperty]
    public partial string LatencyText { get; set; } = string.Empty;

    /// <summary>Version and architecture, shown once in the title bar area.</summary>
    public string VersionText { get; }

    /// <summary>Builds the shell over a started host.</summary>
    public MainWindowViewModel(HostContext host)
    {
        ArgumentNullException.ThrowIfNull(host);

        this.host = host;
        logger = host.LoggerFactory.CreateLogger<MainWindowViewModel>();

        PluginsPage = new PluginsViewModel(host);

        Pages =
        [
            PluginsPage,
            new LogViewModel(host.Logs, host.FileLogger.FilePath),
            new InspectorViewModel(host),
            new SettingsViewModel(host),
        ];

        VersionText = $"{Version()} · {RuntimeInformation.ProcessArchitecture}";

        status = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());

        CurrentPage = PluginsPage;
    }

    /// <summary>
    /// Starts the runtime and fills the list, without blocking the window.
    /// </summary>
    /// <remarks>
    /// Deliberately after the window is up. Bringing twenty plugins up is a sequential walk of
    /// process launches and handshakes and can take several seconds; doing it before the first frame
    /// would show a splash-free application that looks hung, and the log view is the thing a user
    /// wants to be watching while it happens.
    /// </remarks>
    public async Task StartAsync()
    {
        status.Start();

        try
        {
            await host.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The host could not finish starting.");
        }

        PluginsPage.Reload();
        UpdateStatus();
    }

    partial void OnCurrentPageChanged(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        oldValue?.Deactivated();
        newValue?.Activated();
    }

    /// <summary>Switches to a page by type, for the tray menu.</summary>
    [RelayCommand]
    private void Show(string? pageTitle)
    {
        if (Pages.FirstOrDefault(page => string.Equals(page.Title, pageTitle, StringComparison.Ordinal))
            is { } page)
        {
            CurrentPage = page;
        }
    }

    /// <summary>Stops every running plugin without exiting, for the tray menu's Pause.</summary>
    /// <remarks>
    /// Every runner, not the router: pausing is what a user wants before alt-tabbing into an
    /// anti-cheat-protected game they would rather nothing was attached to, and stopping the router
    /// as well would only mean starting the whole host again afterwards.
    /// </remarks>
    [RelayCommand]
    private async Task PauseAllAsync()
    {
        foreach (PluginState state in host.Runtime.Supervisor.States)
        {
            if (state.Status is PluginStatus.Running or PluginStatus.Loaded or PluginStatus.Starting)
                await host.Runtime.Supervisor.StopAsync(state.PluginId, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void UpdateStatus()
    {
        IReadOnlyList<PluginState> states = host.Runtime.Supervisor.States;

        int running = states.Count(state => state.Status == PluginStatus.Running);
        int faulted = states.Count(state =>
            state.Status is PluginStatus.Faulted or PluginStatus.Crashed or PluginStatus.Incompatible);

        long dropped = 0;

        foreach (EdgeSnapshot edge in host.Runtime.Router.Edges)
            dropped += edge.Dropped;

        StatusText = $"{running} running · {states.Count} loaded · {host.Runtime.Problems.Count} problem(s)"
            + (faulted > 0 ? $" · {faulted} faulted" : string.Empty)
            + (dropped > 0 ? $" · {dropped:N0} frames dropped" : string.Empty);

        LatencySnapshot delivery = host.Runtime.Router.DeliveryLatency.Snapshot();

        LatencyText = delivery.Samples == 0
            ? string.Empty
            : $"latency p50 {delivery.Median.TotalMilliseconds:F2} ms · p99 {delivery.Percentile99.TotalMilliseconds:F2} ms";
    }

    private static string Version()
    {
        string informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        // The SourceLink build stamps a "+<sha>" suffix that is noise in a title bar.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? informational : informational[..plus];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        status.Stop();

        foreach (ViewModelBase page in Pages)
        {
            if (page is IDisposable disposable)
                disposable.Dispose();
        }
    }
}
