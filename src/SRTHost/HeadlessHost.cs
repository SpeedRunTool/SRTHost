using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SRTHost.Core;
using SRTHost.Core.Discovery;
using SRTHost.Core.Routing;
using SRTHost.Core.Supervision;
using SRTPluginBase.Abstractions;

namespace SRTHost;

/// <summary>
/// <c>SRTHost.exe --headless</c>: the router and supervisor with no window.
/// </summary>
/// <remarks>
/// For OBS setups, autostart, and anyone who wants the JSON feed without a UI on screen. It is also
/// the only way to exercise the whole application in an automated test, since it needs no display
/// and exits on a signal - which is what the Phase 4 checkpoint is measured through.
/// <para>
/// It shares <see cref="HostRuntime"/> with the graphical shell rather than reimplementing any of
/// it, so a bug found here is a bug in the real thing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class HeadlessHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel(args));
            builder.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss.fff ";
            });
        });

        ILogger logger = loggerFactory.CreateLogger("SRTHost");

        logger.LogInformation(
            "SRT Host (headless) - {Architecture}, {Framework}",
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.FrameworkDescription);

        HostRuntimeOptions options = new()
        {
            PluginsDirectory = Argument(args, "--plugins-dir") ?? HostPaths.DefaultPluginsDirectory,
            Supervisor = new SupervisorOptions { RunnerLogLevel = RunnerLogLevel(args) },
        };

        await using HostRuntime runtime = new(loggerFactory, options);

        runtime.Supervisor.StateChanged += state => logger.LogInformation(
            "{PluginId}: {Status}{SubStatus}{Detail}",
            state.PluginId,
            state.Status,
            state.SubStatus == PluginSubStatus.None ? string.Empty : $" ({state.SubStatus})",
            state.Detail is null ? string.Empty : $" - {state.Detail}");

        using CancellationTokenSource stopping = new();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            logger.LogInformation("Stopping.");
            stopping.Cancel();
        };

        try
        {
            await runtime.StartAsync(stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        if (runtime.Discovered.Count == 0)
        {
            logger.LogWarning(
                "No plugins in {Directory}. Nothing to do; the host will idle until stopped.",
                options.PluginsDirectory);
        }

        // A periodic summary rather than a silent process. Headless means nobody is watching a UI,
        // and a router that has quietly stopped delivering looks exactly like one with nothing to
        // deliver.
        using PeriodicTimer report = new(TimeSpan.FromSeconds(30));

        try
        {
            while (await report.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                Report(logger, runtime);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }

        Report(logger, runtime);

        return 0;
    }

    private static void Report(ILogger logger, HostRuntime runtime)
    {
        foreach (EdgeSnapshot edge in runtime.Router.Edges)
        {
            logger.LogInformation(
                "{ProducerId} -> {ConsumerId} on {ChannelId}: {Delivered} delivered, {Dropped} dropped{Degraded}.",
                edge.ProducerId,
                edge.ConsumerId,
                edge.ChannelId,
                edge.Delivered,
                edge.Dropped,
                edge.Degraded ? ", DEGRADED" : string.Empty);
        }

        LatencySnapshot delivery = runtime.Router.DeliveryLatency.Snapshot();

        if (delivery.Samples > 0)
        {
            logger.LogInformation("Producer to consumer latency: {Delivery}", delivery);
            logger.LogInformation("Router overhead: {Routing}", runtime.Router.RoutingLatency.Snapshot());
        }
    }

    private static Microsoft.Extensions.Logging.LogLevel LogLevel(string[] args)
        => Parse(Argument(args, "--log-level"), Microsoft.Extensions.Logging.LogLevel.Information);

    /// <summary>
    /// The level forwarded from runners, which is worth setting separately from the host's own.
    /// </summary>
    /// <remarks>
    /// A producer at 30 Hz and a consumer that logs each payload together produce sixty lines a
    /// second, which drowns everything the host says about wiring, restarts and latency. Turning the
    /// host down to hide that would hide the host's own output too, so the two are separate knobs.
    /// </remarks>
    private static Microsoft.Extensions.Logging.LogLevel RunnerLogLevel(string[] args)
        => Parse(Argument(args, "--runner-log-level"), LogLevel(args));

    private static Microsoft.Extensions.Logging.LogLevel Parse(
        string? value,
        Microsoft.Extensions.Logging.LogLevel fallback)
        => Enum.TryParse(value, ignoreCase: true, out Microsoft.Extensions.Logging.LogLevel level) ? level : fallback;

    /// <summary>Reads <c>--key value</c> or <c>--key=value</c>, matching the runner's own parser.</summary>
    private static string? Argument(string[] args, string key)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return args[index][(key.Length + 1)..];

            if (string.Equals(args[index], key, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1];
        }

        return null;
    }
}
