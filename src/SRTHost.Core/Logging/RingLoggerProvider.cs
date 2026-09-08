using Microsoft.Extensions.Logging;

namespace SRTHost.Core.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes into a <see cref="LogRing"/>.
/// </summary>
/// <remarks>
/// This is what puts both processes' output in one viewer, and it does so without knowing anything
/// about processes: the supervisor already re-emits every forwarded runner record into the host's
/// own <see cref="ILoggerFactory"/> under <c>SRTHost.Plugin.&lt;pluginId&gt;</c>, so a provider
/// registered on that factory sees host lines and runner lines alike, interleaved in the order they
/// actually arrived.
/// </remarks>
public sealed class RingLoggerProvider(LogRing ring) : ILoggerProvider
{
    private readonly LogRing ring = ring ?? throw new ArgumentNullException(nameof(ring));

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RingLogger(ring, categoryName);

    /// <inheritdoc />
    /// <remarks>The ring outlives the provider: the factory owns the provider, the shell owns the ring.</remarks>
    public void Dispose() { }

    private sealed class RingLogger(LogRing ring, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The factory's own filters have already run by the time Log is called; answering true here
        // keeps this provider from adding a second, quieter opinion about what the user asked to see.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

            ring.Append(new LogEntry
            {
                Timestamp = DateTimeOffset.Now,
                Level = logLevel,
                Category = category,
                Message = formatter(state, exception),
                Exception = exception?.ToString(),
                PluginId = LogEntry.PluginIdFromCategory(category),
            });
        }
    }
}
