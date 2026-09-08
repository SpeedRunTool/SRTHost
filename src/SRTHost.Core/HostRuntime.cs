using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SRTHost.Core.Discovery;
using SRTHost.Core.Routing;
using SRTHost.Core.Supervision;

namespace SRTHost.Core;

/// <summary>How the host was asked to run.</summary>
public sealed record HostRuntimeOptions
{
    /// <summary>Where to look for plugins.</summary>
    public string PluginsDirectory { get; init; } = HostPaths.DefaultPluginsDirectory;

    /// <summary>Supervision tuning.</summary>
    public SupervisorOptions Supervisor { get; init; } = new();

    /// <summary>Plugin ids to leave alone, for a user who has disabled one.</summary>
    public IReadOnlySet<string> DisabledPlugins { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The whole non-visual host: discovery, routing and supervision, wired together.
/// </summary>
/// <remarks>
/// Everything the application does apart from drawing. The Avalonia shell and <c>--headless</c> are
/// two front ends over this one object, which is what makes the headless mode nearly free rather
/// than a second implementation - and what lets the phase checkpoint be exercised without a window.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HostRuntime : IAsyncDisposable
{
    private readonly ILogger<HostRuntime> logger;
    private readonly HostRuntimeOptions options;

    private int disposed;

    /// <summary>Creates the runtime. Nothing starts until <see cref="StartAsync"/>.</summary>
    public HostRuntime(ILoggerFactory loggerFactory, HostRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.options = options ?? new HostRuntimeOptions();

        logger = loggerFactory.CreateLogger<HostRuntime>();
        Router = new IpcRouter(loggerFactory.CreateLogger<IpcRouter>());
        Supervisor = new PluginSupervisor(Router, loggerFactory, this.options.Supervisor);
    }

    /// <summary>The data plane.</summary>
    public IpcRouter Router { get; }

    /// <summary>The process plane.</summary>
    public PluginSupervisor Supervisor { get; }

    /// <summary>Where this host is looking for plugins.</summary>
    public string PluginsDirectory => options.PluginsDirectory;

    /// <summary>Whatever the last scan could not use, and why.</summary>
    public IReadOnlyList<DiscoveryProblem> Problems { get; private set; } = [];

    /// <summary>Plugins the last scan found and this host can run.</summary>
    public IReadOnlyList<DiscoveredPlugin> Discovered { get; private set; } = [];

    /// <summary>
    /// Scans for plugins and brings up everything that is not disabled.
    /// </summary>
    /// <remarks>
    /// Plugins are started sequentially rather than in parallel. Twenty runners racing to spawn at
    /// once turns a cold start into a thundering herd against the disk and the antivirus scanner,
    /// and the wall-clock saving would be spent waiting on the same contention. It also keeps the
    /// startup log in an order a person can read.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Supervisor.Start();

        DiscoveryResult scan = PluginDiscovery.Scan(options.PluginsDirectory);

        Discovered = scan.Plugins;
        Problems = scan.Problems;

        logger.LogInformation(
            "Found {Count} plugin(s) in {Directory}.",
            scan.Plugins.Count,
            options.PluginsDirectory);

        foreach (DiscoveryProblem problem in scan.Problems)
            logger.LogWarning("Ignoring {Path}: {Reason}", problem.Path, problem.Reason);

        // Producers first, so a consumer's first tick has something to bind to. The router wires a
        // pair together whichever order they arrive in, so this only avoids a brief window in which
        // a required-channel consumer has nothing - but that window is visible to a user watching an
        // overlay come up.
        foreach (DiscoveredPlugin plugin in scan.Plugins
            .OrderByDescending(candidate => candidate.Manifest.Kind == SRTPluginBase.Abstractions.PluginKind.Producer)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (options.DisabledPlugins.Contains(plugin.Id))
            {
                logger.LogInformation("Skipping {PluginId}; it is disabled.", plugin.Id);
                continue;
            }

            await Supervisor.AddAsync(plugin, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        // Supervisor first: it stops the runners, which stops anything arriving at the router.
        await Supervisor.DisposeAsync().ConfigureAwait(false);
        await Router.DisposeAsync().ConfigureAwait(false);
    }
}
