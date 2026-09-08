using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SRTHost.Core.Logging;

/// <summary>How the log file behaves.</summary>
public sealed record FileLoggerOptions
{
    /// <summary>Directory to write into.</summary>
    public string Directory { get; init; } = HostPaths.LogDirectory;

    /// <summary>How many log files to keep, including the one being written.</summary>
    /// <remarks>
    /// One file per launch, so this is "the last N runs" rather than a duration. A streamer's host
    /// runs for hours and is launched a few times a day; ten runs is roughly a week of history and a
    /// few megabytes.
    /// </remarks>
    public int Retain { get; init; } = 10;
}

/// <summary>
/// Writes every log record to <c>%LOCALAPPDATA%\SRTHost\logs\</c>, one file per launch.
/// </summary>
/// <remarks>
/// The successor to <c>Issue/35</c>'s <c>FileLogger</c>, rewritten rather than salvaged for one
/// reason: that one wrote to a shared <see cref="StreamWriter"/> from whatever thread logged, and
/// this host logs from every runner's pipe read loop at once. Interleaved half-lines in the file you
/// are reading to diagnose a crash is the worst failure mode a log has.
/// <para>
/// So writes go through a queue drained by one background task. That also keeps disk latency off the
/// pipe read loops - a stalled write must never become backpressure on a producer - and it is why
/// the queue is bounded: if the writer cannot keep up, dropping log lines is correct and blocking
/// the data plane is not.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Lines buffered before further ones are dropped.</summary>
    private const int QueueCapacity = 8192;

    private readonly BlockingCollection<string> queue = new(QueueCapacity);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task writer;
    private readonly FileLoggerOptions options;

    private int disposed;

    /// <summary>Opens this run's log file and starts the writer.</summary>
    public FileLoggerProvider(FileLoggerOptions? options = null)
    {
        this.options = options ?? new FileLoggerOptions();

        System.IO.Directory.CreateDirectory(this.options.Directory);

        // Sortable, so the retention sweep below can order by name and never has to trust a
        // timestamp on disk that a copy or a restore may have rewritten.
        FilePath = Path.Combine(
            this.options.Directory,
            $"SRTHost-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        Prune();

        writer = Task.Run(DrainAsync);
    }

    /// <summary>The file being written this run.</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        queue.CompleteAdding();

        // Bounded so a wedged disk cannot make closing the window hang. Anything still queued is
        // lost, which is the right trade at shutdown: it is also in the ring and on the console.
        if (!writer.Wait(TimeSpan.FromSeconds(2)))
            lifetime.Cancel();

        lifetime.Dispose();
        queue.Dispose();
    }

    private void Enqueue(string line)
    {
        // TryAdd, never Add: Add blocks the caller when the queue is full, and the callers here are
        // the pipe read loops that carry every payload in the application.
        if (!queue.IsAddingCompleted)
            queue.TryAdd(line);
    }

    private async Task DrainAsync()
    {
        try
        {
            await using StreamWriter file = new(
                new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (string line in queue.GetConsumingEnumerable(lifetime.Token))
            {
                await file.WriteLineAsync(line).ConfigureAwait(false);

                // Flushed when the queue empties rather than per line. A crash loses at most what is
                // in the buffer, and the interesting lines before a crash are followed by a pause.
                if (queue.Count == 0)
                    await file.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log file that cannot be written must never take the application with it; the ring
            // and the console still have everything.
            Console.Error.WriteLine($"SRT Host could not write {FilePath}: {ex.Message}");
        }
    }

    /// <summary>Deletes all but the most recent <see cref="FileLoggerOptions.Retain"/> log files.</summary>
    private void Prune()
    {
        try
        {
            foreach (FileInfo stale in new DirectoryInfo(options.Directory)
                .GetFiles("SRTHost-*.log")
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(Math.Max(options.Retain - 1, 0)))
            {
                stale.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log file held open by an editor, or a second host running. Neither is worth failing
            // startup over.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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

            string line =
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";

            provider.Enqueue(exception is null ? line : line + Environment.NewLine + exception);
        }
    }
}
