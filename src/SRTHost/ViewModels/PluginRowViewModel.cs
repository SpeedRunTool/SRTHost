using System.Diagnostics;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using SRTHost.Core.Discovery;
using SRTHost.Core.Routing;
using SRTHost.Core.Supervision;
using SRTPluginBase.Abstractions;

namespace SRTHost.ViewModels;

/// <summary>
/// One plugin, as a row in the list and as the subject of the detail pane.
/// </summary>
/// <remarks>
/// It holds a <see cref="DiscoveredPlugin"/> - which never changes - and the latest
/// <see cref="PluginState"/>, which is replaced wholesale on every change. Rather than mirroring
/// every field into an observable property, the state is one property and the display properties are
/// computed from it: a single <c>OnPropertyChanged(nameof(State))</c> then refreshes the whole row,
/// and there is no way for a field to be forgotten and go stale.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class PluginRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial PluginState State { get; set; }

    /// <summary>Delivered frames across every edge this plugin is part of.</summary>
    [ObservableProperty]
    public partial long Delivered { get; set; }

    /// <summary>Dropped frames across every edge this plugin is part of.</summary>
    [ObservableProperty]
    public partial long Dropped { get; set; }

    /// <summary>Whether any edge is far enough behind to be worth flagging.</summary>
    [ObservableProperty]
    public partial bool Degraded { get; set; }

    /// <summary>The runner's private working set, or null when it is not running.</summary>
    [ObservableProperty]
    public partial long? WorkingSet { get; set; }

    /// <summary>How long the current runner process has been up.</summary>
    [ObservableProperty]
    public partial TimeSpan? Uptime { get; set; }

    /// <summary>Every edge this plugin publishes into or receives from.</summary>
    public System.Collections.ObjectModel.ObservableCollection<EdgeSnapshot> Edges { get; } = [];

    /// <summary>Creates a row for a discovered plugin.</summary>
    public PluginRowViewModel(DiscoveredPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        Plugin = plugin;
        State = new PluginState { PluginId = plugin.Id, Architecture = plugin.Architecture };
    }

    /// <summary>What discovery found on disk. Immutable for the life of the row.</summary>
    public DiscoveredPlugin Plugin { get; }

    /// <summary>The plugin id, which is also its folder name and its config file name.</summary>
    public string Id => Plugin.Id;

    /// <summary>The display name from the manifest.</summary>
    public string Name => Plugin.Manifest.Name;

    /// <summary>Producer or consumer, derived at build time from the interface it implements.</summary>
    public PluginKind Kind => Plugin.Manifest.Kind;

    /// <summary>Which runner architecture it needs.</summary>
    public PluginArchitecture Architecture => Plugin.Manifest.Architecture;

    /// <summary>The plugin's own version.</summary>
    public string Version => Plugin.Manifest.Version;

    /// <summary>Who wrote it.</summary>
    public string Author => Plugin.Manifest.Author;

    /// <summary>What it says it does.</summary>
    public string Description => Plugin.Manifest.Description;

    /// <summary>The folder it was loaded from.</summary>
    public string Directory => Plugin.Directory;

    /// <summary>Status, sub-status and detail as one line for the row.</summary>
    public string StatusText => State.SubStatus == PluginSubStatus.None
        ? State.Status.ToString()
        : $"{State.Status} ({State.SubStatus})";

    /// <summary>Why it is not running, when it is not.</summary>
    public string? Detail => State.Detail;

    /// <summary>The runner's process id, or a dash.</summary>
    public string ProcessText => State.ProcessId?.ToString() ?? "-";

    /// <summary>Restarts inside the current window.</summary>
    public int RestartCount => State.RestartCount;

    /// <summary>Whether the plugin's source - usually the game - is currently there.</summary>
    public bool SourceAvailable => State.SourceAvailable;

    /// <summary>Whether Start would do anything.</summary>
    public bool CanStart => State.Status
        is PluginStatus.NotLoaded or PluginStatus.Stopped or PluginStatus.Faulted or PluginStatus.Crashed;

    /// <summary>Whether Stop would do anything.</summary>
    public bool CanStop => !CanStart && State.Status != PluginStatus.Incompatible;

    /// <summary>A colour key the row's status dot binds to.</summary>
    /// <remarks>
    /// A string rather than a brush so the view models stay free of Avalonia types and testable; the
    /// view maps it through a resource. The three-way split is what a person scanning the list needs:
    /// working, busy, broken.
    /// </remarks>
    public string StatusSeverity => State.Status switch
    {
        PluginStatus.Running when Degraded => "warning",
        PluginStatus.Running => "ok",
        PluginStatus.Loading or PluginStatus.Loaded or PluginStatus.Starting
            or PluginStatus.Stopping or PluginStatus.Unloading => "busy",
        PluginStatus.Stopped or PluginStatus.NotLoaded => "idle",
        _ => "error",
    };

    /// <summary>Working set as something a person reads, or a dash.</summary>
    public string WorkingSetText => WorkingSet is { } bytes ? $"{bytes / (1024 * 1024)} MB" : "-";

    /// <summary>Uptime as something a person reads, or a dash.</summary>
    public string UptimeText => Uptime is { } uptime
        ? uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
            : $"{uptime.Minutes}m {uptime.Seconds}s"
        : "-";

    /// <summary>Applies a state change from the supervisor.</summary>
    public void Apply(PluginState state) => State = state;

    /// <summary>
    /// Refreshes the counters that nothing pushes: router edges and the runner's process metrics.
    /// </summary>
    /// <remarks>
    /// Polled rather than pushed, at one hertz from the list. These change on every frame - thirty
    /// times a second per producer - so an event per change would be an event per frame, and the
    /// only consumer is a label a person glances at.
    /// </remarks>
    public void Refresh(IReadOnlyList<EdgeSnapshot> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        long delivered = 0;
        long dropped = 0;
        bool degraded = false;

        Edges.Clear();

        foreach (EdgeSnapshot edge in edges)
        {
            if (!string.Equals(edge.ProducerId, Id, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(edge.ConsumerId, Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Edges.Add(edge);

            delivered += edge.Delivered;
            dropped += edge.Dropped;
            degraded |= edge.Degraded;
        }

        Delivered = delivered;
        Dropped = dropped;
        Degraded = degraded;

        RefreshProcess();
    }

    private void RefreshProcess()
    {
        if (State.ProcessId is not { } pid)
        {
            WorkingSet = null;
            Uptime = null;
            return;
        }

        try
        {
            using Process runner = Process.GetProcessById(pid);

            WorkingSet = runner.WorkingSet64;
            Uptime = DateTime.Now - runner.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The runner exited between the state update and this poll. The supervisor will say so
            // through StateChanged in a moment; showing a dash until then is honest.
            WorkingSet = null;
            Uptime = null;
        }
    }

    partial void OnStateChanged(PluginState value)
    {
        // Everything the row shows is computed from State, so one change notification per field is
        // the price of not having to remember to raise them individually.
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ProcessText));
        OnPropertyChanged(nameof(RestartCount));
        OnPropertyChanged(nameof(SourceAvailable));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(StatusSeverity));
    }

    partial void OnDegradedChanged(bool value) => OnPropertyChanged(nameof(StatusSeverity));

    partial void OnWorkingSetChanged(long? value) => OnPropertyChanged(nameof(WorkingSetText));

    partial void OnUptimeChanged(TimeSpan? value) => OnPropertyChanged(nameof(UptimeText));
}
