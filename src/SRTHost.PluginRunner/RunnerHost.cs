using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.PluginRunner;

/// <summary>
/// The runner's half of the protocol: one plugin, one connection, one process.
/// </summary>
/// <remarks>
/// The router drives everything. This type answers <see cref="HelloMessage"/>, loads and starts the
/// plugin when told to, publishes what a producer produces, delivers what a consumer subscribes to,
/// and exits when asked. It holds no policy of its own - restart backoff, subscription matching and
/// health decisions all live in the supervisor, because a runner that formed its own opinion about
/// when to come back would be arguing with the process that owns that decision.
/// </remarks>
internal sealed class RunnerHost : IAsyncDisposable
{
    /// <summary>Default producer tick interval.</summary>
    /// <remarks>
    /// 33 ms, i.e. ~30 Hz, which is what the overlays actually redraw at. Stated once, here:
    /// generation 1 had 33 in the field initialiser, 66 in the out-of-range reset and 66 again in
    /// its help text, so two of the three places that documented the default were wrong.
    /// </remarks>
    public const int DefaultProduceIntervalMilliseconds = 33;

    /// <summary>Fastest tick accepted.</summary>
    /// <remarks>
    /// 8 ms is ~125 Hz, comfortably past any display refresh worth chasing and already faster than
    /// the memory reads a producer does per tick. Below this the runner spends its time on timer
    /// wakeups rather than on work.
    /// </remarks>
    public const int MinimumProduceIntervalMilliseconds = 8;

    /// <summary>Slowest tick accepted.</summary>
    public const int MaximumProduceIntervalMilliseconds = 2000;

    /// <summary>How often an unprompted liveness signal is sent.</summary>
    /// <remarks>
    /// One second, against the supervisor's 5 second "unresponsive" mark - four missed beats before
    /// anybody worries, which survives a garbage collection pause and a paging storm without
    /// declaring a healthy runner dead.
    /// </remarks>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private readonly RunnerOptions options;
    private readonly IpcLoggerProvider loggerProvider;
    private readonly ILogger logger;
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private readonly TaskCompletionSource exitRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stopping = new();

    private IpcConnection? connection;
    private LoadedPlugin? plugin;

    private CancellationTokenSource? produceLoop;
    private Task? produceTask;
    private PeriodicTimer? produceTimer;
    private int produceIntervalMilliseconds = DefaultProduceIntervalMilliseconds;

    private ulong sequence;
    private bool sourceAvailable;
    private int disposed;

    /// <summary>Creates the host. Nothing happens until <see cref="RunAsync"/>.</summary>
    public RunnerHost(RunnerOptions options, IpcLoggerProvider loggerProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.loggerProvider = loggerProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Connects to the router and serves it until it says to stop or the pipe breaks.
    /// </summary>
    /// <returns>The process exit code.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);

        Stream pipe = await IpcPipe
            .ConnectAsync(options.PipeName, options.ConnectTimeout, linked.Token)
            .ConfigureAwait(false);

        connection = new IpcConnection(pipe)
        {
            OnControlMessage = HandleControlAsync,
            OnDataFrame = HandleDataAsync,
        };

        loggerProvider.Attach(connection);
        logger.LogInformation("Connected to {PipeName}.", options.PipeName);

        Task reading = connection.RunAsync(linked.Token);
        Task heartbeat = HeartbeatAsync(linked.Token);

        Task finished = await Task.WhenAny(reading, exitRequested.Task).ConfigureAwait(false);

        await stopping.CancelAsync().ConfigureAwait(false);
        await Swallow(heartbeat).ConfigureAwait(false);

        if (finished == reading)
        {
            // The router went away without saying so. Its supervisor will notice the process exit
            // and apply whatever restart policy the plugin has; nothing to decide here.
            try
            {
                await reading.ConfigureAwait(false);
                logger.LogInformation("The router disconnected.");
            }
            catch (OperationCanceledException)
            {
                // Cancellation is a normal shutdown, not a failure.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The connection to the router failed.");
                return 1;
            }
        }

        return 0;
    }

    private async ValueTask<IpcMessage?> HandleControlAsync(
        IpcMessage message,
        uint correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DispatchControlAsync(message, correlationId, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginLoadException ex)
        {
            logger.LogError(ex, "{Kind} failed.", message.Kind);

            return new ErrorMessage
            {
                Message = ex.Message,
                SubStatus = ex.SubStatus,
                Detail = ex.InnerException?.ToString(),
            };
        }
        catch (Exception ex)
        {
            // A handler must never throw out of here: the exception would escape into the frame
            // reader and tear down a connection that is otherwise perfectly healthy, turning a
            // failed operation into a crashed plugin.
            logger.LogError(ex, "{Kind} failed.", message.Kind);

            return new ErrorMessage
            {
                Message = ex.Message,
                SubStatus = PluginSubStatus.UndefinedException,
                Detail = ex.ToString(),
            };
        }
    }

    private async ValueTask<IpcMessage?> DispatchControlAsync(
        IpcMessage message,
        uint correlationId,
        CancellationToken cancellationToken)
    {
        switch (message)
        {
            case HelloMessage hello:
                return Handshake(hello);

            case LoadPluginMessage load:
                return await LoadAsync(load, cancellationToken).ConfigureAwait(false);

            case StartPluginMessage:
                await StartAsync(cancellationToken).ConfigureAwait(false);
                return new AckMessage();

            case StopPluginMessage:
                await StopAsync(cancellationToken).ConfigureAwait(false);
                return new AckMessage();

            case ApplyConfigurationMessage apply:
                await PluginLoader
                    .ApplyConfigurationAsync(Required(), apply.ConfigurationJson, cancellationToken)
                    .ConfigureAwait(false);
                return new AckMessage();

            case GetConfigurationSchemaMessage:
                return DescribeConfiguration();

            case SetProduceIntervalMessage interval:
                SetProduceInterval(interval.IntervalMilliseconds);
                return new AckMessage();

            case ChannelClosedMessage closed:
                await NotifyChannelClosedAsync(closed.ChannelId, cancellationToken).ConfigureAwait(false);
                return new AckMessage();

            case PingMessage:
                return new AckMessage();

            case UnloadPluginMessage:
            case ShutdownMessage:
                await ExitAsync(message, correlationId, cancellationToken).ConfigureAwait(false);
                return null;

            default:
                return new ErrorMessage
                {
                    Message = $"A runner does not handle {message.Kind}.",
                    SubStatus = PluginSubStatus.UndefinedException,
                };
        }
    }

    private IpcMessage Handshake(HelloMessage hello)
    {
        if (hello.ProtocolVersion != IpcProtocol.Version)
        {
            // Refused rather than negotiated. There is exactly one version in the wild and both
            // executables ship in the same installer; a mismatch means an in-place update left them
            // out of step, which a restart fixes and a compatibility shim would hide.
            return new ErrorMessage
            {
                Message = $"The router speaks protocol {hello.ProtocolVersion}; this runner speaks {IpcProtocol.Version}.",
                SubStatus = PluginSubStatus.ContractGenerationMismatch,
            };
        }

        logger.LogInformation("Handshake with session {SessionId}.", hello.SessionId);

        return new HelloAckMessage
        {
            ProcessId = Environment.ProcessId,
            Architecture = RunnerArchitecture,
            RuntimeVersion = Environment.Version.ToString(),
            ProtocolVersion = IpcProtocol.Version,
            ContractGeneration = SrtContract.Generation,
        };
    }

    private async Task<IpcMessage> LoadAsync(LoadPluginMessage request, CancellationToken cancellationToken)
    {
        if (plugin is not null)
        {
            return new ErrorMessage
            {
                Message = $"This runner already hosts '{plugin.Id}'. A runner hosts exactly one plugin.",
                SubStatus = PluginSubStatus.UndefinedException,
            };
        }

        await NotifyStatusAsync(PluginStatus.Loading, cancellationToken: cancellationToken).ConfigureAwait(false);

        plugin = await PluginLoader
            .LoadAsync(request, loggerProvider, options.LogLevel, stopping.Token)
            .ConfigureAwait(false);

        await NotifyStatusAsync(PluginStatus.Loaded, cancellationToken: cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Loaded {PluginId} v{Version} ({Kind}) from {Directory}.",
            plugin.Info.Id,
            plugin.Info.Version,
            plugin.Info.Kind,
            request.PluginDirectory);

        // Ready is returned as the answer to LoadPlugin rather than sent as a separate notification.
        // The router needs the channel list before it can wire anything up, and correlating it to
        // the request it answers means there is no window in which the plugin is loaded but the
        // router does not yet know what it publishes.
        // The settings description rides along rather than waiting to be asked for. The host can then
        // draw a form the instant the user selects the plugin, and the description is refreshed by
        // the same restart that picks up a new version of the plugin.
        SchemaExtractor.ConfigurationDescription? settings = plugin.Configurable is { } configurable
            ? SchemaExtractor.Describe(configurable)
            : null;

        return new ReadyMessage
        {
            PluginKind = plugin.Info.Kind,
            Channel = Describe(plugin.Producer?.Channel),
            Subscriptions = Describe(plugin.Consumer?.Subscriptions),
            RequiresUiThread = plugin.Info.RequiresUiThread,
            ConfigurationSchemaJson = settings?.SchemaJson,
            ConfigurationHintsJson = settings?.HintsJson,
            ConfigurationJson = settings?.CurrentJson,
        };
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        LoadedPlugin loaded = Required();

        if (loaded.IsStarted)
            return;

        await NotifyStatusAsync(PluginStatus.Starting, cancellationToken: cancellationToken).ConfigureAwait(false);

        await loaded.Dispatcher.InvokeAsync(
            async token => await loaded.Instance.StartAsync(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        loaded.IsStarted = true;

        if (loaded.Producer is not null)
        {
            produceLoop = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            produceTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(produceIntervalMilliseconds));
            produceTask = ProduceAsync(loaded, produceTimer, produceLoop.Token);
        }

        await NotifyStatusAsync(PluginStatus.Running, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        LoadedPlugin loaded = Required();

        if (!loaded.IsStarted)
            return;

        await NotifyStatusAsync(PluginStatus.Stopping, cancellationToken: cancellationToken).ConfigureAwait(false);
        await StopProducingAsync().ConfigureAwait(false);

        await loaded.Dispatcher.InvokeAsync(
            async token => await loaded.Instance.StopAsync(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        loaded.IsStarted = false;
        sourceAvailable = false;

        await NotifyStatusAsync(PluginStatus.Stopped, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task StopProducingAsync()
    {
        if (produceLoop is not null)
            await produceLoop.CancelAsync().ConfigureAwait(false);

        if (produceTask is not null)
            await Swallow(produceTask).ConfigureAwait(false);

        produceTimer?.Dispose();
        produceLoop?.Dispose();

        produceTimer = null;
        produceLoop = null;
        produceTask = null;
    }

    /// <summary>
    /// The producer tick: read the source, publish what changed.
    /// </summary>
    /// <remarks>
    /// It skips entirely while <see cref="IProducerPlugin.IsSourceAvailable"/> is false, so a
    /// producer with no game running costs a timer wakeup and nothing else - generation 1 polled and
    /// serialised unconditionally, which is why it showed measurable CPU with an empty desktop.
    /// <para>
    /// There is deliberately no "pause when nobody is subscribed" check here, even though the design
    /// calls for that behaviour: subscriber count is the router's knowledge, and the router already
    /// has a way to say it - <see cref="StopPluginMessage"/>. Inventing a second, weaker channel for
    /// the same fact would give two places to be wrong about whether a producer should be running.
    /// </para>
    /// </remarks>
    private async Task ProduceAsync(LoadedPlugin loaded, PeriodicTimer timer, CancellationToken cancellationToken)
    {
        IProducerPlugin producer = loaded.Producer!;
        ArrayBufferWriter<byte> buffer = new(initialCapacity: 16 * 1024);
        PayloadCodec codec = producer.Channel.Codec;
        string channelId = producer.Channel.ChannelId;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                bool available = producer.IsSourceAvailable;

                if (available != sourceAvailable)
                {
                    sourceAvailable = available;
                    await SendAsync(
                        new SourceAvailabilityChangedMessage { Available = available },
                        cancellationToken).ConfigureAwait(false);
                }

                if (!available)
                    continue;

                buffer.ResetWrittenCount();

                bool produced = await loaded.Dispatcher
                    .InvokeAsync(token => producer.TryProduceAsync(buffer, token), cancellationToken)
                    .ConfigureAwait(false);

                if (!produced || buffer.WrittenCount == 0)
                    continue;

                DataFrameHeader header = new()
                {
                    Sequence = sequence++,
                    TimestampUtcTicks = DateTime.UtcNow.Ticks,
                    ChannelId = channelId,
                };

                await connection!
                    .SendDataAsync(header, new ReadOnlySequence<byte>(buffer.WrittenMemory), codec, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped or shutting down.
        }
        catch (Exception ex)
        {
            // A producer that throws mid-tick is a plugin bug, and continuing to tick would repeat
            // it thirty times a second. Report it and stop; the supervisor decides what happens next.
            logger.LogError(ex, "The producer tick failed; publishing has stopped.");

            await Swallow(SendAsync(
                new FaultMessage { Message = ex.Message, StackTrace = ex.ToString() },
                CancellationToken.None).AsTask()).ConfigureAwait(false);

            await Swallow(NotifyStatusAsync(
                PluginStatus.Faulted,
                PluginSubStatus.UndefinedException,
                ex.Message,
                CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>Delivers one payload frame to the consumer this runner hosts.</summary>
    /// <remarks>
    /// Awaited inline on the read loop, which does apply backpressure to the pipe - and that is the
    /// intended shape. The router holds a bounded latest-value-wins queue per edge and drops frames
    /// for a consumer that cannot keep up, so a slow consumer costs itself frames rather than
    /// stalling the producer. Queueing again here would only add a second buffer with the same
    /// policy and hide how far behind the consumer actually is.
    /// </remarks>
    private async ValueTask HandleDataAsync(
        DataFrameHeader header,
        ReadOnlySequence<byte> payload,
        PayloadCodec codec,
        CancellationToken cancellationToken)
    {
        if (plugin?.Consumer is not { } consumer)
            return;

        byte[]? rented = null;
        ReadOnlyMemory<byte> memory;

        if (payload.IsSingleSegment)
        {
            memory = payload.First;
        }
        else
        {
            // A frame large enough to straddle segments has to be made contiguous before a plugin
            // can be handed a ReadOnlyMemory over it. Rented rather than allocated, because at 30 Hz
            // an allocation per frame is a garbage collection every few seconds.
            rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
            payload.CopyTo(rented);
            memory = rented.AsMemory(0, (int)payload.Length);
        }

        try
        {
            PayloadFrame frame = new()
            {
                ChannelId = header.ChannelId,
                Sequence = (long)header.Sequence,
                TimestampTicks = header.TimestampUtcTicks,
                Codec = codec,
                Payload = memory,
            };

            await plugin.Dispatcher.InvokeAsync(
                async token => await consumer.ConsumeAsync(frame, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // One bad frame must not kill the connection. A consumer throwing on a payload it cannot
            // read is a plugin bug worth reporting, but the next frame may well be fine and the
            // alternative is losing the whole plugin over it.
            logger.LogError(ex, "The consumer threw while handling a frame on {ChannelId}.", header.ChannelId);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task NotifyChannelClosedAsync(string channelId, CancellationToken cancellationToken)
    {
        if (plugin?.Consumer is not { } consumer)
            return;

        await plugin.Dispatcher.InvokeAsync(
            async token => await consumer.OnChannelClosedAsync(channelId, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    private IpcMessage DescribeConfiguration()
    {
        LoadedPlugin loaded = Required();

        if (loaded.Configurable is not { } configurable)
        {
            return new ErrorMessage
            {
                Message = $"'{loaded.Id}' has no settings.",
                SubStatus = PluginSubStatus.None,
            };
        }

        // The same three documents the load reply already carried. This path exists to re-read the
        // current values, which change whenever settings are applied, and it costs one reflection
        // pass over a small type - so it is not worth caching what Ready already sent.
        SchemaExtractor.ConfigurationDescription settings = SchemaExtractor.Describe(configurable);

        return new ConfigurationSchemaMessage
        {
            SchemaJson = settings.SchemaJson,
            HintsJson = settings.HintsJson,
            CurrentJson = settings.CurrentJson,
        };
    }

    private void SetProduceInterval(int milliseconds)
    {
        produceIntervalMilliseconds = Math.Clamp(
            milliseconds,
            MinimumProduceIntervalMilliseconds,
            MaximumProduceIntervalMilliseconds);

        if (produceIntervalMilliseconds != milliseconds)
        {
            logger.LogWarning(
                "Produce interval {Requested} ms is outside {Minimum}-{Maximum} ms; using {Applied} ms.",
                milliseconds,
                MinimumProduceIntervalMilliseconds,
                MaximumProduceIntervalMilliseconds,
                produceIntervalMilliseconds);
        }

        // Applied to the live timer rather than taking effect at the next start: the setting is
        // reachable from the UI while a producer is running, which is the only time anybody adjusts
        // it.
        if (produceTimer is not null)
            produceTimer.Period = TimeSpan.FromMilliseconds(produceIntervalMilliseconds);
    }

    private async Task ExitAsync(IpcMessage request, uint correlationId, CancellationToken cancellationToken)
    {
        string reason = request switch
        {
            ShutdownMessage shutdown => shutdown.Reason ?? "the router asked",
            UnloadPluginMessage => "the plugin was unloaded",
            _ => "unknown",
        };

        logger.LogInformation("Shutting down: {Reason}.", reason);

        // Acknowledged from inside the handler rather than by returning the message, so the write is
        // known to have completed before the process starts coming down. Returning it would leave
        // the send racing the exit, and a router that never sees its acknowledgement has to fall
        // back on a kill timeout for what was a clean shutdown.
        await SendAsync(new AckMessage(), correlationId, cancellationToken).ConfigureAwait(false);

        await StopProducingAsync().ConfigureAwait(false);
        exitRequested.TrySetResult();
    }

    private async Task NotifyStatusAsync(
        PluginStatus status,
        PluginSubStatus subStatus = PluginSubStatus.None,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(
            new PluginStatusChangedMessage { Status = status, SubStatus = subStatus, Detail = detail },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(HeartbeatInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendAsync(
                    new HeartbeatMessage { UptimeMilliseconds = (long)uptime.Elapsed.TotalMilliseconds },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Best-effort report of an exception that is about to end the process.
    /// </summary>
    /// <remarks>
    /// Blocking, and bounded, and called from an unhandled-exception handler - so all three of the
    /// usual objections to blocking on async work are answered by the situation. The router must not
    /// depend on this arriving: the runner may already be too broken to write to a pipe, which is
    /// why the supervisor watches for process exit rather than for this message.
    /// </remarks>
    public void ReportFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            Console.Error.WriteLine(exception);

            connection?
                .SendAsync(new FaultMessage
                {
                    Message = exception.Message,
                    StackTrace = exception.ToString(),
                    Fatal = true,
                })
                .AsTask()
                .Wait(FatalFlushTimeout);
        }
        catch
        {
            // Nothing left to try. The process is going down either way.
        }
    }

    /// <summary>How long a dying runner spends trying to get its last words onto the pipe.</summary>
    private static readonly TimeSpan FatalFlushTimeout = TimeSpan.FromMilliseconds(250);

    private ValueTask SendAsync(IpcMessage message, CancellationToken cancellationToken)
        => SendAsync(message, correlationId: 0, cancellationToken);

    private async ValueTask SendAsync(IpcMessage message, uint correlationId, CancellationToken cancellationToken)
    {
        if (connection is { } target)
            await target.SendAsync(message, correlationId, cancellationToken).ConfigureAwait(false);
    }

    private LoadedPlugin Required()
        => plugin ?? throw new PluginLoadException(
            PluginSubStatus.UndefinedException,
            "No plugin has been loaded in this runner yet.");

    private static ChannelDescriptor? Describe(PayloadChannelDescriptor? channel)
        => channel is null
            ? null
            : new ChannelDescriptor
            {
                ChannelId = channel.ChannelId,
                ContractVersion = $"{channel.ContractVersion.Major}.{Math.Max(channel.ContractVersion.Minor, 0)}",
                Codec = channel.Codec,
                SchemaJson = channel.SchemaJson,
            };

    private static IReadOnlyList<SubscriptionDescriptor> Describe(IReadOnlyList<ChannelSubscription>? subscriptions)
        => subscriptions is null
            ? []
            : [.. subscriptions.Select(subscription => new SubscriptionDescriptor
            {
                ChannelId = subscription.ChannelId,
                MinimumContractVersion =
                    $"{subscription.MinimumContractVersion.Major}.{Math.Max(subscription.MinimumContractVersion.Minor, 0)}",
                Required = subscription.Required,
            })];

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Used only on teardown paths, where the caller has already decided the outcome does not
            // change what happens next.
        }
    }

#if x64
    private const PluginArchitecture RunnerArchitecture = PluginArchitecture.X64;
#else
    private const PluginArchitecture RunnerArchitecture = PluginArchitecture.X86;
#endif

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await stopping.CancelAsync().ConfigureAwait(false);
        await StopProducingAsync().ConfigureAwait(false);

        if (plugin is not null)
            await plugin.DisposeAsync().ConfigureAwait(false);

        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);

        stopping.Dispose();
    }
}
