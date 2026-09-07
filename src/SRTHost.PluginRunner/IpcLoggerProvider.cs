using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SRTHost.Ipc;

namespace SRTHost.PluginRunner;

/// <summary>
/// An <see cref="ILoggerProvider"/> that forwards everything a plugin logs to the host over the IPC
/// log channel.
/// </summary>
/// <remarks>
/// This is what makes a runner's output land in the same file and the same log view as the host's,
/// and it is the whole replacement for generation 1's <c>IPluginHostDelegates.OutputMessage</c> /
/// <c>ExceptionMessage</c> pair: a plugin now takes an <see cref="ILogger"/> like any other .NET
/// component and never learns that a process boundary exists.
/// <para>
/// Records go through a bounded queue drained by one background task rather than being written
/// inline. Logging is synchronous and a pipe write is not, so the alternative is blocking on an
/// async write inside <see cref="ILogger.Log"/> - which a plugin may well call from its render loop.
/// The queue is bounded and drops the oldest record when full for the same reason payload frames
/// are: a plugin that logs faster than the pipe drains must not be able to stall the thing it is
/// logging about, and the newest records are the ones worth keeping.
/// </para>
/// </remarks>
internal sealed class IpcLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    /// <summary>
    /// How many records may be queued before the oldest are dropped.
    /// </summary>
    /// <remarks>
    /// Sized for a burst - a plugin logging an exception per frame for a couple of seconds - not for
    /// a sustained flood, which is a plugin bug that a bigger buffer would only hide for longer.
    /// </remarks>
    private const int QueueCapacity = 1024;

    private readonly Channel<IpcLogRecord> queue = Channel.CreateBounded<IpcLogRecord>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly LogLevel minimumLevel;
    private readonly CancellationTokenSource lifetime = new();

    private IpcConnection? connection;
    private Task? drain;
    private int disposed;

    /// <summary>Creates the provider. Nothing is sent until <see cref="Attach"/> is called.</summary>
    public IpcLoggerProvider(LogLevel minimumLevel) => this.minimumLevel = minimumLevel;

    /// <summary>
    /// Starts forwarding over <paramref name="ipcConnection"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from construction. The logger has to exist before the connection does -
    /// connecting is itself worth logging, and so is failing to - so records written in between are
    /// queued here and flushed the moment there is somewhere to send them.
    /// </remarks>
    public void Attach(IpcConnection ipcConnection)
    {
        ArgumentNullException.ThrowIfNull(ipcConnection);

        connection = ipcConnection;
        drain ??= Task.Run(() => DrainAsync(lifetime.Token));
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new IpcLogger(this, categoryName);

    private bool IsEnabled(LogLevel level) => level >= minimumLevel && level != LogLevel.None;

    private void Enqueue(IpcLogRecord record)
    {
        if (!queue.Writer.TryWrite(record))
            WriteFallback(record, "queue closed");
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (IpcLogRecord record in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                IpcConnection? target = connection;

                if (target is null)
                {
                    WriteFallback(record, "not connected");
                    continue;
                }

                try
                {
                    await target.SendLogAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The connection is gone, which is not this component's problem to solve - the
                    // read loop will notice and the process will come down. Until then, stderr is
                    // still captured by the router, so keep writing there rather than losing the
                    // records that explain what happened.
                    connection = null;
                    WriteFallback(record, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private static void WriteFallback(IpcLogRecord record, string reason)
    {
        // stderr rather than stdout: the router captures it separately and attaches the tail of it
        // to a crash report, which is exactly the situation this path exists for.
        Console.Error.WriteLine(
            $"[{record.Timestamp:HH:mm:ss.fff}] {record.Level} {record.Category}: {record.Message} ({reason})");

        if (record.Exception is not null)
            Console.Error.WriteLine(record.Exception);
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        queue.Writer.TryComplete();

        if (drain is not null)
        {
            // Bounded, because a shutdown that hangs waiting to flush logs is worse than a shutdown
            // that loses the last few records.
            try
            {
                await drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
        }

        lifetime.Dispose();
    }

    /// <summary>One category's worth of logging, forwarded over IPC.</summary>
    private sealed class IpcLogger(IpcLoggerProvider provider, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
                return;

            provider.Enqueue(new IpcLogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = logLevel,
                Category = category,
                Message = formatter(state, exception),
                EventId = eventId.Id,

                // Flattened here rather than sent as a type: an exception from a plugin's private
                // dependency closure has no meaning in the host process, which cannot load the
                // assembly that declares it. The formatted text is the part that is actually useful
                // in a log view.
                Exception = exception?.ToString(),
            });
        }
    }
}
