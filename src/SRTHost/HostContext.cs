using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SRTHost.Core;
using SRTHost.Core.Logging;

namespace SRTHost;

/// <summary>
/// Everything the shell is built out of, constructed once and handed to the view models.
/// </summary>
/// <remarks>
/// A hand-built composition root rather than a DI container. The graph is five objects deep and
/// fully known at startup; a container would add a package, a registration list and a lifetime model
/// to express what a constructor already says. The plugin runners <em>do</em> use a container,
/// because there a plugin's constructor arguments are genuinely unknown until the assembly is loaded.
/// <para>
/// It also owns the shutdown order, which is the part that matters: the logger factory has to outlive
/// the runtime, because the runtime logs while it is being torn down.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HostContext : IAsyncDisposable
{
    private int disposed;

    /// <summary>Builds the host from stored settings, with command-line overrides applied.</summary>
    public HostContext(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Settings = settings;
        Logs = new LogRing();

        FileLogger = new FileLoggerProvider(new FileLoggerOptions { Retain = settings.LogRetention });

        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            // The minimum level has to be the more permissive of the two knobs, because a filter
            // cannot re-enable what the minimum level already dropped - the categories are then told
            // apart by the rule below.
            builder.SetMinimumLevel(
                (LogLevel)Math.Min((int)settings.LogLevel, (int)settings.RunnerLogLevel));

            builder.AddFilter((category, level) =>
                category is not null && category.StartsWith(LogEntry.PluginCategoryPrefix, StringComparison.Ordinal)
                    ? level >= settings.RunnerLogLevel
                    : level >= settings.LogLevel);

            builder.AddProvider(new RingLoggerProvider(Logs));
            builder.AddProvider(FileLogger);
        });

        Runtime = new HostRuntime(LoggerFactory, settings.ToRuntimeOptions());
    }

    /// <summary>The settings this host was built from.</summary>
    /// <remarks>
    /// A snapshot, not a live object. Most of what is in here is read once during construction -
    /// the plugins directory, the log providers, the supervisor's restart policy - so a settings
    /// page that mutated it in place would show changes that had not taken effect. The page saves to
    /// disk and says plainly which fields need a restart.
    /// </remarks>
    public HostSettings Settings { get; private set; }

    /// <summary>The in-memory log the viewer renders.</summary>
    public LogRing Logs { get; }

    /// <summary>This run's log file, for the "open log folder" command.</summary>
    public FileLoggerProvider FileLogger { get; }

    /// <summary>The factory every part of the host logs through, including forwarded runner output.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Discovery, routing and supervision.</summary>
    public HostRuntime Runtime { get; }

    /// <summary>Records settings the user has saved, so the shell reflects them without a restart.</summary>
    public void ApplySavedSettings(HostSettings settings) => Settings = settings;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        // Runtime first: it stops the runners, and it logs while doing it.
        await Runtime.DisposeAsync().ConfigureAwait(false);

        LoggerFactory.Dispose();
    }
}
