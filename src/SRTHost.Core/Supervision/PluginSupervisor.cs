using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SRTHost.Core.Discovery;
using SRTHost.Core.Routing;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Supervision;

/// <summary>Tuning for <see cref="PluginSupervisor"/>.</summary>
public sealed record SupervisorOptions
{
    /// <summary>How long a runner may go without saying anything before it is probed.</summary>
    /// <remarks>
    /// Five seconds against a one-second heartbeat: four missed beats before anybody worries, which
    /// survives a garbage collection pause or a paging storm without declaring a healthy runner
    /// dead.
    /// </remarks>
    public TimeSpan UnresponsiveAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a silent runner gets before it is killed outright.</summary>
    public TimeSpan KillAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>First restart delay; it doubles from here.</summary>
    public TimeSpan InitialRestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest a restart is ever delayed.</summary>
    public TimeSpan MaximumRestartDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many restarts inside <see cref="RestartWindow"/> before giving up.</summary>
    /// <remarks>
    /// A plugin that crashes five times in five minutes is not going to be fixed by a sixth attempt;
    /// it needs a person. Restarting forever would hide the failure behind a process that keeps
    /// appearing to come back.
    /// </remarks>
    public int MaximumRestarts { get; init; } = 5;

    /// <summary>The window restarts are counted over.</summary>
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Restart policy applied to every plugin.</summary>
    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.OnCrash;

    /// <summary>Minimum level forwarded from runners.</summary>
    public LogLevel RunnerLogLevel { get; init; } = LogLevel.Information;
}

/// <summary>
/// Owns every runner process: starts them, watches them, restarts them, and stops them.
/// </summary>
/// <remarks>
/// The half of the host that has opinions. <see cref="IpcRouter"/> moves payloads and
/// <see cref="PluginRunnerProcess"/> reports what a runner said; everything about whether a plugin
/// should be running, and what to do when it stops being, is decided here.
/// <para>
/// Reload is a process recycle rather than an assembly-load-context unload. That is simpler and
/// strictly more thorough - native handles, unmanaged memory, static state and the game handle all
/// go away with the process - and it unlocks the plugin's DLL, which is what makes an in-app update
/// possible at all. Generation 1's <c>Reload</c> was a no-op.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PluginSupervisor : IAsyncDisposable
{
    private readonly IpcRouter router;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<PluginSupervisor> logger;
    private readonly SupervisorOptions options;
    private readonly JobObject job = new();
    private readonly string sessionId = IpcProtocol.NewSessionId();
    private readonly ConcurrentDictionary<string, Supervised> supervised = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();

    private Task? watchdog;
    private int disposed;

    /// <summary>Creates a supervisor.</summary>
    public PluginSupervisor(
        IpcRouter router,
        ILoggerFactory loggerFactory,
        SupervisorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.router = router;
        this.loggerFactory = loggerFactory;
        this.options = options ?? new SupervisorOptions();

        logger = loggerFactory.CreateLogger<PluginSupervisor>();
    }

    /// <summary>The per-launch session id that every pipe name is built from.</summary>
    public string SessionId => sessionId;

    /// <summary>Raised whenever a plugin's state changes, for the UI to bind to.</summary>
    public event Action<PluginState>? StateChanged;

    /// <summary>The current state of every plugin under supervision.</summary>
    public IReadOnlyList<PluginState> States => [.. supervised.Values.Select(entry => entry.State)];

    /// <summary>Starts the watchdog. Call once, before adding plugins.</summary>
    public void Start() => watchdog ??= Task.Run(() => WatchAsync(lifetime.Token), CancellationToken.None);

    /// <summary>
    /// Brings <paramref name="plugin"/> up: spawn, handshake, load, register, start.
    /// </summary>
    /// <returns>Whether it reached <see cref="PluginStatus.Running"/>.</returns>
    public async Task<bool> AddAsync(DiscoveredPlugin plugin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        Supervised entry = supervised.GetOrAdd(plugin.Id, _ => new Supervised(plugin));

        return await LaunchAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops a plugin and forgets it.</summary>
    public async Task RemoveAsync(string pluginId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!supervised.TryRemove(pluginId, out Supervised? entry))
            return;

        await TearDownAsync(entry, "removed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recycles a plugin's process, which is what "reload" means here.
    /// </summary>
    public async Task<bool> ReloadAsync(string pluginId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!supervised.TryGetValue(pluginId, out Supervised? entry))
            return false;

        await TearDownAsync(entry, "reloading", cancellationToken).ConfigureAwait(false);

        // A deliberate reload does not count against the crash budget: the user asked for it, and
        // spending their restart allowance on it would mean five reloads disabled a healthy plugin.
        entry.Restarts.Clear();

        return await LaunchAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> LaunchAsync(Supervised entry, CancellationToken cancellationToken)
    {
        Update(entry, state => state with
        {
            Status = PluginStatus.Loading,
            SubStatus = PluginSubStatus.None,
            Detail = null,
            Architecture = entry.Plugin.Architecture,
        });

        try
        {
            PluginRunnerProcess runner = await PluginRunnerProcess.StartAsync(
                entry.Plugin,
                sessionId,
                job,
                loggerFactory,
                options.RunnerLogLevel,
                cancellationToken).ConfigureAwait(false);

            entry.Runner = runner;
            runner.Notified += OnRunnerNotified;

            string? configuration = ReadConfiguration(entry.Plugin.Id);

            await runner.LoadAsync(configuration, cancellationToken).ConfigureAwait(false);

            Update(entry, state => state with
            {
                Status = PluginStatus.Loaded,
                ProcessId = runner.ProcessId,
            });

            await runner.StartAsync(router, cancellationToken).ConfigureAwait(false);

            Update(entry, state => state with { Status = PluginStatus.Running });

            // Watching for exit is attached only after a successful start. Attaching earlier would
            // race the launch itself and could schedule a restart for a runner that is still coming
            // up.
            _ = Task.Run(() => WatchForExitAsync(entry, runner), CancellationToken.None);

            return true;
        }
        catch (Exception ex)
        {
            PluginSubStatus subStatus = ex switch
            {
                IpcRequestException request => request.SubStatus,
                TimeoutException => PluginSubStatus.RunnerCrashed,
                FileNotFoundException => PluginSubStatus.DependencyNotFound,
                _ => PluginSubStatus.UndefinedException,
            };

            logger.LogError(ex, "Could not start {PluginId}.", entry.Plugin.Id);

            Update(entry, state => state with
            {
                Status = PluginStatus.Faulted,
                SubStatus = subStatus,
                Detail = ex.Message,
                ProcessId = null,
            });

            if (entry.Runner is { } failed)
            {
                failed.Notified -= OnRunnerNotified;
                await failed.DisposeAsync().ConfigureAwait(false);
                entry.Runner = null;
            }

            return false;
        }
    }

    private async Task WatchForExitAsync(Supervised entry, PluginRunnerProcess runner)
    {
        await runner.Exited.ConfigureAwait(false);

        runner.Notified -= OnRunnerNotified;

        // A runner that exited is no longer a routing destination, and its subscribers need to be
        // told rather than left showing whatever it last published.
        await router.UnregisterAsync(entry.Plugin.Id).ConfigureAwait(false);

        if (lifetime.IsCancellationRequested)
            return;

        bool expected = runner.ShutdownRequested;

        await runner.DisposeAsync().ConfigureAwait(false);

        if (entry.Runner == runner)
            entry.Runner = null;

        if (expected)
        {
            Update(entry, state => state with { Status = PluginStatus.Stopped, ProcessId = null });
            return;
        }

        logger.LogWarning("The runner for {PluginId} exited unexpectedly.", entry.Plugin.Id);

        Update(entry, state => state with
        {
            Status = PluginStatus.Crashed,
            SubStatus = PluginSubStatus.RunnerCrashed,
            ProcessId = null,
        });

        await RestartAsync(entry).ConfigureAwait(false);
    }

    /// <summary>
    /// Restarts a crashed plugin with exponential backoff, or gives up and says so.
    /// </summary>
    /// <remarks>
    /// Backoff exists because the common crash is deterministic - a plugin that faults on the game's
    /// current state will fault again immediately - and an unthrottled restart loop turns that into
    /// a process being spawned as fast as the machine allows.
    /// </remarks>
    private async Task RestartAsync(Supervised entry)
    {
        if (options.RestartPolicy == RestartPolicy.Never)
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        while (entry.Restarts.TryPeek(out DateTimeOffset oldest) && now - oldest > options.RestartWindow)
            entry.Restarts.TryDequeue(out _);

        if (entry.Restarts.Count >= options.MaximumRestarts)
        {
            logger.LogError(
                "{PluginId} has restarted {Count} times in {Window}; giving up.",
                entry.Plugin.Id,
                entry.Restarts.Count,
                options.RestartWindow);

            Update(entry, state => state with
            {
                Status = PluginStatus.Faulted,
                SubStatus = PluginSubStatus.RestartLimitExceeded,
                Detail = $"Restarted {entry.Restarts.Count} times within {options.RestartWindow}.",
            });

            return;
        }

        entry.Restarts.Enqueue(now);

        TimeSpan delay = TimeSpan.FromTicks(Math.Min(
            options.InitialRestartDelay.Ticks << (entry.Restarts.Count - 1),
            options.MaximumRestartDelay.Ticks));

        logger.LogInformation(
            "Restarting {PluginId} in {Delay} (attempt {Attempt}).",
            entry.Plugin.Id,
            delay,
            entry.Restarts.Count);

        Update(entry, state => state with { RestartCount = entry.Restarts.Count });

        try
        {
            await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await LaunchAsync(entry, lifetime.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Notices runners that have gone quiet, probes them, and eventually kills them.
    /// </summary>
    /// <remarks>
    /// A crashed runner is easy - the process exits and the pipe breaks. This covers the harder
    /// case: a runner that is still alive and still holding its pipe open but no longer doing
    /// anything, which a plugin deadlocked on a game handle produces routinely. Nothing else would
    /// ever notice it.
    /// </remarks>
    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (Supervised entry in supervised.Values)
                {
                    if (entry.Runner is not { } runner)
                        continue;

                    TimeSpan silence = DateTimeOffset.UtcNow - runner.LastContact;

                    if (silence > options.KillAfter)
                    {
                        logger.LogError(
                            "{PluginId} has not responded for {Silence}; terminating its runner.",
                            entry.Plugin.Id,
                            silence);

                        // Killed rather than asked politely: it has already failed to answer a ping,
                        // so there is no reason to believe it would answer a shutdown either. The
                        // exit watcher turns this into a restart.
                        await runner.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    if (silence <= options.UnresponsiveAfter || entry.ProbeInFlight)
                        continue;

                    entry.ProbeInFlight = true;

                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await runner.PingAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "{PluginId} did not answer a ping.", entry.Plugin.Id);
                            }
                            finally
                            {
                                entry.ProbeInFlight = false;
                            }
                        },
                        CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void OnRunnerNotified(PluginRunnerProcess runner, IpcMessage message)
    {
        if (!supervised.TryGetValue(runner.PluginId, out Supervised? entry))
            return;

        switch (message)
        {
            case PluginStatusChangedMessage status:
                Update(entry, state => state with
                {
                    Status = status.Status,
                    SubStatus = status.SubStatus,
                    Detail = status.Detail,
                });
                break;

            case SourceAvailabilityChangedMessage availability:
                Update(entry, state => state with { SourceAvailable = availability.Available });
                break;

            case HeartbeatMessage:
                Update(entry, state => state with { LastHeartbeat = DateTimeOffset.UtcNow }, raise: false);
                break;

            case FaultMessage fault:
                logger.LogError("{PluginId} faulted: {Message}", runner.PluginId, fault.Message);

                Update(entry, state => state with
                {
                    Status = fault.Fatal ? PluginStatus.Crashed : PluginStatus.Faulted,
                    SubStatus = PluginSubStatus.UndefinedException,
                    Detail = fault.Message,
                });
                break;
        }
    }

    private async Task TearDownAsync(Supervised entry, string reason, CancellationToken cancellationToken)
    {
        if (entry.Runner is not { } runner)
            return;

        Update(entry, state => state with { Status = PluginStatus.Unloading });

        await router.UnregisterAsync(entry.Plugin.Id).ConfigureAwait(false);
        await runner.ShutdownAsync(reason, cancellationToken).ConfigureAwait(false);
        await runner.DisposeAsync().ConfigureAwait(false);

        entry.Runner = null;

        Update(entry, state => state with { Status = PluginStatus.Stopped, ProcessId = null });
    }

    /// <summary>
    /// Reads a plugin's stored settings, if it has any.
    /// </summary>
    /// <remarks>
    /// Read as text and passed through without being parsed. The host cannot deserialise it - the
    /// configuration type lives in the plugin's own assembly, which the host never loads - and it
    /// has no reason to try.
    /// </remarks>
    private string? ReadConfiguration(string pluginId)
    {
        string path = HostPaths.ConfigFile(pluginId);

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read settings for {PluginId}; starting with defaults.", pluginId);
            return null;
        }
    }

    private void Update(Supervised entry, Func<PluginState, PluginState> change, bool raise = true)
    {
        PluginState updated = change(entry.State);
        entry.State = updated;

        if (raise)
            StateChanged?.Invoke(updated);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await lifetime.CancelAsync().ConfigureAwait(false);

        // Stopped in parallel: twenty plugins at up to five seconds of grace each would otherwise
        // make closing the window take a minute and a half.
        await Task.WhenAll(supervised.Values.Select(async entry =>
        {
            try
            {
                await TearDownAsync(entry, "the host is shutting down", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Tearing down {PluginId} failed.", entry.Plugin.Id);
            }
        })).ConfigureAwait(false);

        supervised.Clear();

        // Last, and the backstop for everything above: closing the job handle terminates any runner
        // still alive, however it got that way.
        job.Dispose();
        lifetime.Dispose();
    }

    /// <summary>One plugin's supervision state.</summary>
    private sealed class Supervised(DiscoveredPlugin plugin)
    {
        public DiscoveredPlugin Plugin { get; } = plugin;

        public PluginRunnerProcess? Runner { get; set; }

        public PluginState State { get; set; } = new() { PluginId = plugin.Id };

        /// <summary>Timestamps of recent restarts, trimmed to the restart window.</summary>
        public ConcurrentQueue<DateTimeOffset> Restarts { get; } = new();

        /// <summary>Whether a liveness probe is already outstanding, so the watchdog does not pile them up.</summary>
        public bool ProbeInFlight { get; set; }
    }
}
