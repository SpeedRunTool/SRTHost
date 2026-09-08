using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SRTHost.Core;
using SRTHost.Core.Discovery;
using SRTHost.Core.Routing;
using SRTHost.Core.Supervision;

namespace SRTHost.ViewModels;

/// <summary>
/// The plugin list and the detail pane beside it - the page the shell opens on.
/// </summary>
/// <remarks>
/// Every router and supervisor operation is reachable from here, which is the Phase 5 checkpoint:
/// start, stop, reload, remove, rescan, open the folder, and see what discovery refused to load and
/// why.
/// <para>
/// The supervisor raises <see cref="PluginSupervisor.StateChanged"/> from whichever pipe read loop
/// or watchdog tick noticed the change, so every handler here marshals to the UI thread before
/// touching a collection. That is the one threading rule in this file and it is not optional: an
/// <see cref="ObservableCollection{T}"/> mutated off the UI thread throws inside Avalonia's binding
/// layer, at a moment - a plugin crashing - when the user most needs the window to still work.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class PluginsViewModel : ViewModelBase, IDisposable
{
    private readonly HostContext host;
    private readonly ILogger logger;
    private readonly DispatcherTimer refresh;

    /// <summary>Every plugin currently known, in the order discovery found them.</summary>
    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    /// <summary>What the last scan refused to load, and why.</summary>
    /// <remarks>
    /// On the page rather than buried in the log because "my plugin is not in the list" is the
    /// single most common support question a plugin host gets, and the answer is always here: a
    /// mismatched folder name, an unsafe id, a contract generation this host cannot load.
    /// </remarks>
    public ObservableCollection<DiscoveryProblem> Problems { get; } = [];

    /// <summary>Whether the discovery-problems panel has anything to show.</summary>
    /// <remarks>
    /// A bool rather than binding the collection's count, because a binding to <c>Count</c> would
    /// need a converter to reach <c>IsVisible</c> and would not update - <c>Count</c> raises no
    /// property change of its own on an <see cref="ObservableCollection{T}"/>.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasProblems { get; set; }

    [ObservableProperty]
    public partial PluginRowViewModel? Selected { get; set; }

    /// <summary>Whether a start, stop or reload is in flight, so the buttons can disable themselves.</summary>
    [ObservableProperty]
    public partial bool Busy { get; set; }

    /// <summary>Creates the page and subscribes to the supervisor.</summary>
    public PluginsViewModel(HostContext host)
    {
        ArgumentNullException.ThrowIfNull(host);

        this.host = host;
        logger = host.LoggerFactory.CreateLogger<PluginsViewModel>();

        host.Runtime.Supervisor.StateChanged += OnStateChanged;

        // One hertz for counters nothing pushes - edge totals, working set, uptime. Fast enough to
        // watch a plugin come up, slow enough to be invisible next to a 30 Hz data plane.
        refresh = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshCounters());
    }

    /// <inheritdoc />
    public override string Title => "Plugins";

    /// <inheritdoc />
    public override void Activated() => refresh.Start();

    /// <inheritdoc />
    public override void Deactivated() => refresh.Stop();

    /// <summary>Rebuilds the list from what the runtime currently knows.</summary>
    /// <remarks>
    /// Called once after the runtime starts and again after every rescan. It reconciles rather than
    /// clearing: a row carries the selection and the counters, and rebuilding the collection would
    /// deselect whatever the user was looking at every time a plugin was added.
    /// </remarks>
    public void Reload()
    {
        Dispatcher.UIThread.VerifyAccess();

        foreach (DiscoveredPlugin discovered in host.Runtime.Discovered)
        {
            if (Plugins.Any(row => string.Equals(row.Id, discovered.Id, StringComparison.OrdinalIgnoreCase)))
                continue;

            Plugins.Add(new PluginRowViewModel(discovered));
        }

        for (int index = Plugins.Count - 1; index >= 0; index--)
        {
            if (!host.Runtime.Discovered.Any(discovered =>
                string.Equals(discovered.Id, Plugins[index].Id, StringComparison.OrdinalIgnoreCase)))
            {
                Plugins.RemoveAt(index);
            }
        }

        Problems.Clear();

        foreach (DiscoveryProblem problem in host.Runtime.Problems)
            Problems.Add(problem);

        HasProblems = Problems.Count > 0;

        // The supervisor already has state for anything that started before this page existed.
        foreach (PluginState state in host.Runtime.Supervisor.States)
            Find(state.PluginId)?.Apply(state);

        Selected ??= Plugins.FirstOrDefault();

        RefreshCounters();
    }

    [RelayCommand]
    private async Task StartAsync(PluginRowViewModel? row)
    {
        if (row is null)
            return;

        await RunAsync(
            row,
            // A plugin that was removed, or was never started because it is disabled, has no
            // supervision entry - so Start has to be able to add one rather than only resume one.
            async token => await host.Runtime.Supervisor.StartAsync(row.Id, token).ConfigureAwait(false)
                || await host.Runtime.Supervisor.AddAsync(row.Plugin, token).ConfigureAwait(false));
    }

    [RelayCommand]
    private async Task StopAsync(PluginRowViewModel? row)
    {
        if (row is null)
            return;

        await RunAsync(row, token => host.Runtime.Supervisor.StopAsync(row.Id, token));
    }

    [RelayCommand]
    private async Task ReloadPluginAsync(PluginRowViewModel? row)
    {
        if (row is null)
            return;

        await RunAsync(row, token => host.Runtime.Supervisor.ReloadAsync(row.Id, token));
    }

    /// <summary>Rescans the plugins directory and starts anything new.</summary>
    [RelayCommand]
    private async Task RescanAsync()
    {
        Busy = true;

        try
        {
            // The runtime's own StartAsync is the scan: it is idempotent per plugin, because
            // AddAsync goes through GetOrAdd and a plugin already supervised keeps its runner.
            await host.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(true);

            Reload();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rescanning the plugins directory failed.");
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Opens a plugin's folder in Explorer.</summary>
    [RelayCommand]
    private void OpenFolder(PluginRowViewModel? row)
        => OpenInExplorer(row?.Directory ?? host.Runtime.PluginsDirectory);

    /// <summary>Opens the plugins directory itself, for the case where the list is empty.</summary>
    [RelayCommand]
    private void OpenPluginsFolder() => OpenInExplorer(host.Runtime.PluginsDirectory);

    private async Task RunAsync(PluginRowViewModel row, Func<CancellationToken, Task<bool>> operation)
    {
        Busy = true;

        try
        {
            // ConfigureAwait(true) throughout this file: the continuation touches observable state
            // and belongs back on the UI thread. Everything it awaits is a supervisor call that
            // spans a process launch and a handshake, so the thread is genuinely released meanwhile.
            await operation(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The operation on {PluginId} failed.", row.Id);
        }
        finally
        {
            Busy = false;
        }
    }

    private void OnStateChanged(PluginState state)
    {
        // Post, not Invoke: this is called from a pipe read loop and from the watchdog, and blocking
        // either on the UI thread would let a slow render become backpressure on the data plane.
        Dispatcher.UIThread.Post(() => Find(state.PluginId)?.Apply(state));
    }

    private void RefreshCounters()
    {
        IReadOnlyList<EdgeSnapshot> edges = host.Runtime.Router.Edges;

        foreach (PluginRowViewModel row in Plugins)
            row.Refresh(edges);
    }

    /// <summary>
    /// Keeps a row selected whenever there is one to select.
    /// </summary>
    /// <remarks>
    /// The detail pane is bound to the selection, so an empty selection hides half the page. That is
    /// not hypothetical: a <c>DataGrid</c> pushes a null selection back through the binding while its
    /// items source is being populated, which lands after the initial <see cref="Reload"/> has chosen
    /// a row and leaves the list open with nothing selected. Restoring it on the dispatcher rather
    /// than inline lets the grid finish what it is doing first.
    /// </remarks>
    partial void OnSelectedChanged(PluginRowViewModel? value)
    {
        if (value is null && Plugins.Count > 0)
            Dispatcher.UIThread.Post(() => Selected ??= Plugins.FirstOrDefault());
    }

    private PluginRowViewModel? Find(string pluginId)
        => Plugins.FirstOrDefault(row => string.Equals(row.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens a folder in the shell, creating it first if it is missing.</summary>
    /// <remarks>
    /// <c>UseShellExecute</c> is required: without it this tries to execute the directory as a
    /// program and fails with a Win32 error rather than opening a window. The directory is created
    /// on the way because the most useful time to press "open plugins folder" is when there are no
    /// plugins in it.
    /// </remarks>
    private void OpenInExplorer(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Path}.", path);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        host.Runtime.Supervisor.StateChanged -= OnStateChanged;
        refresh.Stop();
    }
}
