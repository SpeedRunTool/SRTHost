using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SRTHost.Core.Discovery;
using SRTHost.Core.Routing;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Supervision;

/// <summary>
/// One runner process, from the host's side: the pipe, the connection, and the plugin it hosts.
/// </summary>
/// <remarks>
/// Owns exactly one plugin, mirroring the runner it talks to. It is also the plugin's
/// <see cref="IRouterEndpoint"/>, so the router can deliver to it without knowing a process is
/// involved.
/// <para>
/// It has no restart logic and no opinion about health - that is
/// <see cref="PluginSupervisor"/>'s job. This type reports what happened and stays out of the
/// decision, which keeps "what the runner said" and "what to do about it" in separate places.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PluginRunnerProcess : IRouterEndpoint, IAsyncDisposable
{
    /// <summary>How long to wait for the runner to connect back before giving up on it.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a runner gets to exit after being asked politely.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly Process process;
    private readonly IpcConnection connection;
    private readonly ILogger logger;
    private readonly ILogger pluginLogger;
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();

    private IpcRouter? router;
    private int disposed;

    private PluginRunnerProcess(
        DiscoveredPlugin plugin,
        Process process,
        IpcConnection connection,
        ILogger logger,
        ILogger pluginLogger)
    {
        Plugin = plugin;
        this.process = process;
        this.connection = connection;
        this.logger = logger;
        this.pluginLogger = pluginLogger;
    }

    /// <summary>The plugin this runner hosts.</summary>
    public DiscoveredPlugin Plugin { get; }

    /// <inheritdoc />
    public string PluginId => Plugin.Id;

    /// <summary>The runner's process id.</summary>
    public int ProcessId => process.Id;

    /// <summary>The runner's answer to the opening handshake.</summary>
    public HelloAckMessage? HelloAck { get; private set; }

    /// <summary>What the plugin declared when it loaded.</summary>
    public ReadyMessage? Ready { get; private set; }

    /// <summary>When the runner last said anything.</summary>
    public DateTimeOffset LastContact { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Completes when the runner process has exited.</summary>
    public Task Exited => exited.Task;

    /// <summary>Whether this runner was asked to stop, so its exit is expected.</summary>
    public bool ShutdownRequested { get; private set; }

    /// <summary>Raised for every lifecycle change, fault and availability change the runner reports.</summary>
    public event Action<PluginRunnerProcess, IpcMessage>? Notified;

    /// <summary>
    /// Spawns a runner for <paramref name="plugin"/> and completes the handshake.
    /// </summary>
    /// <remarks>
    /// The pipe server is created <em>before</em> the process is started, so there is no window in
    /// which a fast runner dials a name that does not exist yet. It is also created with
    /// <c>FirstPipeInstance</c>, so a name already in use is a hard failure rather than a silent
    /// share with whatever got there first.
    /// </remarks>
    public static async Task<PluginRunnerProcess> StartAsync(
        DiscoveredPlugin plugin,
        string sessionId,
        JobObject job,
        ILoggerFactory loggerFactory,
        LogLevel runnerLogLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ILogger logger = loggerFactory.CreateLogger<PluginRunnerProcess>();

        // Runner output lands under a category naming the plugin, which is what puts host output and
        // plugin output in one file and one log view without the plugin knowing anything about it.
        ILogger pluginLogger = loggerFactory.CreateLogger($"SRTHost.Plugin.{plugin.Id}");

        string pipeName = IpcProtocol.PipeName(sessionId, plugin.Id);
        string executable = HostPaths.RunnerExecutable(plugin.Architecture);

        if (!File.Exists(executable))
            throw new FileNotFoundException($"The plugin runner '{executable}' is missing from the install.", executable);

        NamedPipeServerStream server = IpcPipe.CreateServer(pipeName);
        Task waiting = server.WaitForConnectionAsync(cancellationToken);

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--log-level");
        startInfo.ArgumentList.Add(runnerLogLevel.ToString());

        // Diagnostic only, and worth the two arguments: it makes a row in Task Manager's command
        // line column say which plugin a runner is, which is the difference between twenty
        // identical processes and twenty identifiable ones.
        startInfo.ArgumentList.Add("--plugin-id");
        startInfo.ArgumentList.Add(plugin.Id);
        startInfo.ArgumentList.Add("--plugin-dir");
        startInfo.ArgumentList.Add(plugin.Directory);

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{executable}'.");

        try
        {
            job.Assign(process);

            await waiting.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            KillQuietly(process);
            await server.DisposeAsync().ConfigureAwait(false);

            throw ex is TimeoutException
                ? new TimeoutException($"The runner for '{plugin.Id}' did not connect within {ConnectTimeout}.", ex)
                : ex;
        }

        PluginRunnerProcess? runner = null;

        IpcConnection connection = new(server)
        {
            OnControlMessage = (message, _, _) =>
            {
                runner!.HandleNotification(message);
                return ValueTask.FromResult<IpcMessage?>(null);
            },
            OnDataFrame = (header, payload, codec, _) =>
            {
                runner!.HandleData(header, payload, codec);
                return ValueTask.CompletedTask;
            },
            OnLogRecord = (record, _) =>
            {
                runner!.HandleLog(record);
                return ValueTask.CompletedTask;
            },
        };

        runner = new PluginRunnerProcess(plugin, process, connection, logger, pluginLogger);
        runner.BeginPumpingOutput();

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await connection.RunAsync(runner.lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "The connection to {PluginId} ended.", plugin.Id);
                }
                finally
                {
                    // A broken pipe means the runner is gone or going. Waiting for the process
                    // object rather than assuming it lets the supervisor read the exit code.
                    runner.OnProcessGone();
                }
            },
            CancellationToken.None);

        _ = Task.Run(
            async () =>
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                runner.OnProcessGone();
            },
            CancellationToken.None);

        runner.HelloAck = await runner.HandshakeAsync(sessionId, cancellationToken).ConfigureAwait(false);

        return runner;
    }

    /// <summary>Loads the plugin and returns what it declared.</summary>
    public async Task<ReadyMessage> LoadAsync(string? configurationJson, CancellationToken cancellationToken)
    {
        IpcMessage reply = await connection.RequestAsync(
            new LoadPluginMessage
            {
                PluginId = Plugin.Id,
                PluginDirectory = Plugin.Directory,
                EntryAssembly = Plugin.Manifest.EntryAssembly,
                EntryType = Plugin.Manifest.EntryType,
                ConfigurationJson = configurationJson,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Ready = reply as ReadyMessage
            ?? throw new IpcProtocolException($"Expected Ready from '{Plugin.Id}', got {reply.Kind}.");

        return Ready;
    }

    /// <summary>Registers the loaded plugin with <paramref name="ipcRouter"/> and starts it.</summary>
    /// <remarks>
    /// Registration happens before <see cref="StartPluginMessage"/> so that a producer's very first
    /// tick already has somewhere to go. The other order loses the opening frames of every session,
    /// which is exactly the moment a user is watching to see whether it works.
    /// </remarks>
    public async Task StartAsync(IpcRouter ipcRouter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ipcRouter);

        ReadyMessage ready = Ready
            ?? throw new InvalidOperationException($"'{Plugin.Id}' has not been loaded yet.");

        router = ipcRouter;

        if (ready.PluginKind == PluginKind.Producer && ready.Channel is { } channel)
            ipcRouter.RegisterProducer(this, channel);
        else if (ready.PluginKind == PluginKind.Consumer)
            ipcRouter.RegisterConsumer(this, ready.Subscriptions);

        await connection.RequestAsync(new StartPluginMessage(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Asks the plugin to stop without unloading it.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
        => await connection.RequestAsync(new StopPluginMessage(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Probes the runner, to tell "busy" apart from "hung".</summary>
    public async Task PingAsync(CancellationToken cancellationToken)
        => await connection.RequestAsync(new PingMessage(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Pushes new settings to the plugin.</summary>
    public async Task ApplyConfigurationAsync(string configurationJson, CancellationToken cancellationToken)
        => await connection.RequestAsync(
            new ApplyConfigurationMessage { ConfigurationJson = configurationJson },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Asks the runner to shut down, then makes sure it did.
    /// </summary>
    /// <remarks>
    /// Politely first, because the plugin has a <c>StopAsync</c> and a <c>DisposeAsync</c> that
    /// release a game handle and, for an overlay, destroy a window on the thread that created it.
    /// Then unconditionally, because a plugin that hangs in its own teardown must not be able to
    /// hang the host - the job object would eventually get it, but only when the host itself dies.
    /// </remarks>
    public async Task ShutdownAsync(string reason, CancellationToken cancellationToken)
    {
        ShutdownRequested = true;

        try
        {
            await connection.RequestAsync(
                new ShutdownMessage { Reason = reason },
                timeout: ShutdownGrace,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await Exited.WaitAsync(ShutdownGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IpcProtocolException or IpcRequestException or ObjectDisposedException)
        {
            logger.LogWarning("{PluginId} did not shut down cleanly ({Reason}); terminating it.", Plugin.Id, ex.Message);
            KillQuietly(process);
        }
    }

    /// <inheritdoc />
    public async ValueTask SendDataAsync(
        DataFrameHeader header,
        ReadOnlyMemory<byte> payload,
        PayloadCodec codec,
        CancellationToken cancellationToken)
    {
        await connection
            .SendDataAsync(header, new ReadOnlySequence<byte>(payload), codec, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask NotifyChannelClosedAsync(string channelId, CancellationToken cancellationToken)
    {
        await connection
            .SendAsync(new ChannelClosedMessage { ChannelId = channelId }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HelloAckMessage> HandshakeAsync(string sessionId, CancellationToken cancellationToken)
    {
        IpcMessage reply = await connection.RequestAsync(
            new HelloMessage
            {
                ProtocolVersion = IpcProtocol.Version,
                ContractGeneration = SrtContract.Generation,
                SessionId = sessionId,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return reply as HelloAckMessage
            ?? throw new IpcProtocolException($"Expected HelloAck from '{Plugin.Id}', got {reply.Kind}.");
    }

    private void HandleNotification(IpcMessage message)
    {
        LastContact = DateTimeOffset.UtcNow;
        Notified?.Invoke(this, message);
    }

    private void HandleData(in DataFrameHeader header, in ReadOnlySequence<byte> payload, PayloadCodec codec)
    {
        LastContact = DateTimeOffset.UtcNow;
        router?.Publish(Plugin.Id, header, payload, codec);
    }

    private void HandleLog(IpcLogRecord record)
    {
        LastContact = DateTimeOffset.UtcNow;

        string message = record.Exception is null
            ? record.Message
            : record.Message + Environment.NewLine + record.Exception;

        // Re-emitted rather than reconstructed. The exception is already formatted text by the time
        // it arrives - the type it came from lives in the plugin's private dependency closure and
        // cannot be loaded here at all - so it is appended rather than passed as an exception.
        pluginLogger.Log(record.Level, new EventId(record.EventId), message, exception: null, static (state, _) => state);
    }

    private void BeginPumpingOutput()
    {
        // Both redirected streams must be drained continuously. A runner that fills its stdout pipe
        // buffer blocks on the write, and a plugin logging to Console - which plenty will, out of
        // habit - is exactly how that happens.
        _ = Task.Run(
            async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    logger.LogWarning("[{PluginId}] {Line}", Plugin.Id, line);
            },
            CancellationToken.None);

        _ = Task.Run(
            async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                    logger.LogDebug("[{PluginId}] {Line}", Plugin.Id, line);
            },
            CancellationToken.None);
    }

    private void OnProcessGone() => exited.TrySetResult();

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the outcome being asked for.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        ShutdownRequested = true;

        await lifetime.CancelAsync().ConfigureAwait(false);
        KillQuietly(process);

        await connection.DisposeAsync().ConfigureAwait(false);

        process.Dispose();
        lifetime.Dispose();
    }
}
