using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SRTHost.Core.Logging;

namespace SRTHost.ViewModels;

/// <summary>
/// One viewer for both processes' output, filtered by level, plugin and text.
/// </summary>
/// <remarks>
/// "Both processes" is free rather than clever: the supervisor already re-emits every record a runner
/// forwards into the host's own <see cref="ILoggerFactory"/>, so a provider on that factory sees
/// everything. This page only has to render it.
/// <para>
/// New entries are batched rather than appended one by one. A runner logging at thirty hertz would
/// otherwise raise thirty collection-changed notifications a second per plugin, each of which is a
/// layout pass over a virtualised list - the classic way a log viewer becomes the most expensive
/// thing in an application. They are drained on a timer instead, so the cost is bounded by the frame
/// rate rather than by the log rate.
/// </para>
/// </remarks>
public sealed partial class LogViewModel : ViewModelBase, IDisposable
{
    /// <summary>How often queued entries are moved into the visible list.</summary>
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(250);

    private readonly LogRing ring;
    private readonly string logFilePath;
    private readonly DispatcherTimer drain;
    private readonly Lock pendingGate = new();

    private List<LogEntry> pending = [];

    /// <summary>The entries currently shown, oldest first.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>Every plugin that has logged this run, plus the "all" sentinel.</summary>
    public ObservableCollection<string> Sources { get; } = [AllSources];

    /// <summary>The levels the level filter offers.</summary>
    public IReadOnlyList<LogLevel> Levels { get; } =
        [LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical];

    [ObservableProperty]
    public partial LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    [ObservableProperty]
    public partial string SelectedSource { get; set; } = AllSources;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Whether the view scrolls to the newest entry as it arrives.</summary>
    [ObservableProperty]
    public partial bool FollowTail { get; set; } = true;

    /// <summary>What the footer says about how much is being shown.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    private const string AllSources = "All sources";

    /// <summary>Creates the viewer over a ring.</summary>
    public LogViewModel(LogRing ring, string logFilePath)
    {
        ArgumentNullException.ThrowIfNull(ring);

        this.ring = ring;
        this.logFilePath = logFilePath;

        drain = new DispatcherTimer(DrainInterval, DispatcherPriority.Background, (_, _) => Drain());

        // Everything already logged during startup - which is where the interesting failures are.
        foreach (LogEntry entry in ring.Snapshot())
            Enqueue(entry);

        ring.Appended += Enqueue;
    }

    /// <inheritdoc />
    public override string Title => "Log";

    /// <inheritdoc />
    public override void Activated() => drain.Start();

    /// <inheritdoc />
    /// <remarks>
    /// The timer stops but the ring subscription stays: entries keep queueing while the page is
    /// hidden, so switching to it shows what happened while you were elsewhere rather than starting
    /// blank. The queue is bounded by the ring's own capacity.
    /// </remarks>
    public override void Deactivated() => drain.Stop();

    /// <summary>Empties the view and the ring behind it.</summary>
    [RelayCommand]
    private void Clear()
    {
        lock (pendingGate)
            pending.Clear();

        ring.Clear();
        Entries.Clear();
        UpdateSummary();
    }

    /// <summary>Copies everything currently visible to the clipboard.</summary>
    /// <remarks>
    /// Visible, not everything: a person who has filtered to one plugin and one level has already
    /// said what they want to paste into a bug report.
    /// </remarks>
    [RelayCommand]
    private async Task CopyAsync()
    {
        StringBuilder text = new();

        foreach (LogEntry entry in Entries)
            text.AppendLine(entry.ToString());

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
        {
            await clipboard.SetTextAsync(text.ToString()).ConfigureAwait(true);
        }
    }

    /// <summary>Opens the folder holding this run's log file, with the file selected.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            // /select, rather than opening the folder: there are up to ten log files in there and
            // the useful one is this run's.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{logFilePath}\""))?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Nothing to do about it, and nowhere useful to say so - this is the log viewer.
        }
    }

    /// <summary>Whether an entry passes the current filters.</summary>
    /// <remarks>Internal rather than private so the filter logic can be tested without a window.</remarks>
    internal bool Matches(LogEntry entry)
    {
        if (entry.Level < MinimumLevel)
            return false;

        if (!string.Equals(SelectedSource, AllSources, StringComparison.Ordinal)
            && !string.Equals(entry.PluginId ?? HostSource, SelectedSource, StringComparison.Ordinal))
        {
            return false;
        }

        return SearchText.Length == 0
            || entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the source filter calls the host's own output.</summary>
    internal const string HostSource = "SRT Host";

    private void Enqueue(LogEntry entry)
    {
        lock (pendingGate)
            pending.Add(entry);
    }

    private void Drain()
    {
        List<LogEntry> batch;

        lock (pendingGate)
        {
            if (pending.Count == 0)
                return;

            batch = pending;
            pending = [];
        }

        foreach (LogEntry entry in batch)
        {
            string source = entry.PluginId ?? HostSource;

            if (!Sources.Contains(source))
                Sources.Add(source);

            if (Matches(entry))
                Entries.Add(entry);
        }

        // The view is capped at the ring's capacity for the same reason the ring is: an unbounded
        // list of rendered rows is a memory leak with a scrollbar.
        while (Entries.Count > ring.Capacity)
            Entries.RemoveAt(0);

        UpdateSummary();
    }

    /// <summary>Re-applies the filters to everything the ring still holds.</summary>
    private void Refilter()
    {
        Entries.Clear();

        foreach (LogEntry entry in ring.Snapshot())
        {
            if (Matches(entry))
                Entries.Add(entry);
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int held = ring.Count;

        Summary = Entries.Count == held
            ? $"{held:N0} entries"
            : $"{Entries.Count:N0} of {held:N0} entries";

        if (ring.Overwritten > 0)
            Summary += $" ({ring.Overwritten:N0} older discarded)";
    }

    partial void OnMinimumLevelChanged(LogLevel value) => Refilter();

    partial void OnSelectedSourceChanged(string value) => Refilter();

    partial void OnSearchTextChanged(string value) => Refilter();

    /// <inheritdoc />
    public void Dispose()
    {
        ring.Appended -= Enqueue;
        drain.Stop();
    }
}
