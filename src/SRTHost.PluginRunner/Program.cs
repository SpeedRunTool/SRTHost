using Microsoft.Extensions.Logging;

namespace SRTHost.PluginRunner;

/// <summary>
/// Entry point for a plugin runner process. Each runner hosts exactly one plugin and talks to the
/// router in SRTHost.exe over a named pipe.
/// </summary>
internal static class Program
{
    /// <summary>Bad or missing command-line arguments.</summary>
    private const int ExitInvalidArguments = 2;

    /// <summary>The router's pipe never accepted a connection.</summary>
    private const int ExitNoRouter = 3;

    private static async Task<int> Main(string[] args)
    {
        if (!RunnerOptions.TryParse(args, out RunnerOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(RunnerOptions.Usage);
            return ExitInvalidArguments;
        }

        await using IpcLoggerProvider loggerProvider = new(options!.LogLevel);

        // The provider is deliberately not handed to the factory to own. It outlives the factory on
        // the fatal path below, where the last thing this process does is try to get a log record
        // and a Fault message onto a pipe that the factory has already been disposed out from under.
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(options.LogLevel);
            builder.AddProvider(new NonDisposingLoggerProvider(loggerProvider));
        });

        ILogger logger = loggerFactory.CreateLogger("SRTHost.PluginRunner");

        await using RunnerHost host = new(options, loggerProvider, logger);

        // A plugin runs arbitrary third-party code, much of it against another process's memory, so
        // an unhandled exception on a thread the runner does not own is a realistic outcome rather
        // than a theoretical one. FailFast rather than a graceful exit is the point: it produces a
        // real crash dump with the faulting stack intact, which is the only artefact that makes a
        // plugin crash diagnosable after the fact. The Fault message is sent first and best-effort,
        // so the host can say which plugin died even when no dump is collected.
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                host.ReportFatal(exception);
                Environment.FailFast($"Unhandled exception in plugin runner: {exception.Message}", exception);
            }

            Environment.FailFast("Unhandled non-exception throw in plugin runner.");
        };

        // Ctrl+C is not how a runner is meant to stop - the router owns its lifetime - but it is how
        // somebody debugging one by hand will stop it, and the plugin deserves its StopAsync and
        // DisposeAsync either way.
        using CancellationTokenSource cancellation = new();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await host.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            logger.LogError(ex, "No router was listening on {PipeName}.", options.PipeName);
            return ExitNoRouter;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Wraps a provider so an <see cref="ILoggerFactory"/> cannot dispose it.
    /// </summary>
    /// <remarks>
    /// <see cref="ILoggerFactory"/> takes ownership of every provider added to it, and the runner
    /// needs the IPC provider to outlive the factory by exactly one step: the fatal path logs after
    /// everything else has been torn down.
    /// </remarks>
    private sealed class NonDisposingLoggerProvider(ILoggerProvider inner) : ILoggerProvider
    {
        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
