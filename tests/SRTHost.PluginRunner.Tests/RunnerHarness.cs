using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.PluginRunner.Tests;

/// <summary>
/// Drives a real <c>SRTHost.PluginRunner</c> process the way the router will.
/// </summary>
/// <remarks>
/// Deliberately an out-of-process harness rather than a set of unit tests over the runner's internal
/// types. Every failure mode this phase is exposed to - a plugin that will not load out of a
/// directory, an architecture mismatch, a handshake that never completes, a process that does not
/// exit - only exists once there really are two processes and a pipe between them. Testing the
/// runner in-process would answer none of those questions while looking like it had.
/// <para>
/// It stands in for the router that Phase 4 will build, and the messages it sends are the ones the
/// router will send, so it doubles as executable documentation of the protocol's happy path.
/// </para>
/// </remarks>
internal sealed class RunnerHarness : IAsyncDisposable
{
    /// <summary>Generous, because a first run pays assembly loading and antivirus on cold files.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly List<IpcLogRecord> logs = [];
    private readonly List<IpcMessage> notifications = [];
    private readonly List<(DataFrameHeader Header, byte[] Payload, PayloadCodec Codec)> frames = [];
    private readonly SemaphoreSlim arrived = new(0);
    private readonly StringBuilder standardError = new();
    private readonly Lock gate = new();

    private RunnerHarness(Process process, IpcConnection connection, Task reading)
    {
        Process = process;
        Connection = connection;
        Reading = reading;
    }

    /// <summary>The runner process.</summary>
    public Process Process { get; }

    /// <summary>The router's end of the pipe.</summary>
    public IpcConnection Connection { get; }

    /// <summary>The read loop, so a protocol failure surfaces rather than hanging a test.</summary>
    public Task Reading { get; }

    /// <summary>Whatever the runner wrote to stderr, for a failure message worth reading.</summary>
    public string StandardError
    {
        get
        {
            lock (gate)
                return standardError.ToString();
        }
    }

    /// <summary>Starts a runner and completes the opening handshake.</summary>
    public static async Task<RunnerHarness> StartAsync(
        CancellationToken cancellationToken,
        string logLevel = "Debug")
    {
        string sessionId = IpcProtocol.NewSessionId();
        string pipeName = IpcProtocol.PipeName(sessionId, "SpeedRunTool.Demo.Harness");

        NamedPipeServerStream server = IpcPipe.CreateServer(pipeName);
        Task waiting = server.WaitForConnectionAsync(cancellationToken);

        ProcessStartInfo startInfo = new(RunnerLocator.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // The runner resolves nothing relative to its working directory, but pinning it to the
            // test's output makes a stray relative path show up here instead of somewhere in the
            // build tree.
            WorkingDirectory = AppContext.BaseDirectory,
        };

        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--log-level");
        startInfo.ArgumentList.Add(logLevel);

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{RunnerLocator.ExecutablePath}'.");

        RunnerHarness? harness = null;

        try
        {
            await waiting.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        IpcConnection connection = null!;

        connection = new IpcConnection(server)
        {
            OnControlMessage = (message, _, _) =>
            {
                harness!.Record(message);
                return ValueTask.FromResult<IpcMessage?>(null);
            },
            OnDataFrame = (header, payload, codec, _) =>
            {
                harness!.Record(header, payload.ToArray(), codec);
                return ValueTask.CompletedTask;
            },
            OnLogRecord = (record, _) =>
            {
                harness!.Record(record);
                return ValueTask.CompletedTask;
            },
        };

        Task reading = Task.Run(async () =>
        {
            try
            {
                await connection.RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The pipe breaking is how a runner exit is observed; tests assert on the exit code.
            }
        }, CancellationToken.None);

        harness = new RunnerHarness(process, connection, reading);

        _ = Task.Run(
            async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    lock (harness.gate)
                        harness.standardError.AppendLine(line);
                }
            },
            CancellationToken.None);

        // Drain stdout so a chatty runner cannot fill its pipe buffer and block.
        _ = Task.Run(() => process.StandardOutput.ReadToEndAsync(), CancellationToken.None);

        HelloAckMessage ack = await harness
            .RequestAsync<HelloAckMessage>(
                new HelloMessage
                {
                    ProtocolVersion = IpcProtocol.Version,
                    ContractGeneration = SrtContract.Generation,
                    SessionId = sessionId,
                },
                cancellationToken)
            .ConfigureAwait(false);

        harness.HelloAck = ack;

        return harness;
    }

    /// <summary>The runner's answer to the opening handshake.</summary>
    public HelloAckMessage HelloAck { get; private set; } = null!;

    /// <summary>Sends a request and asserts the reply is of the expected type.</summary>
    public async Task<TReply> RequestAsync<TReply>(IpcMessage request, CancellationToken cancellationToken)
        where TReply : IpcMessage
    {
        IpcMessage reply = await Connection
            .RequestAsync(request, Timeout, cancellationToken)
            .ConfigureAwait(false);

        return Assert.IsType<TReply>(reply);
    }

    /// <summary>Loads the demo plugin whose folder is named <paramref name="pluginId"/>.</summary>
    public Task<ReadyMessage> LoadAsync(string pluginId, string entryType, CancellationToken cancellationToken)
        => RequestAsync<ReadyMessage>(
            new LoadPluginMessage
            {
                PluginId = pluginId,
                PluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins", pluginId),
                EntryAssembly = pluginId + ".dll",
                EntryType = entryType,
            },
            cancellationToken);

    /// <summary>Publishes a payload to the runner, as the router does for a consumer.</summary>
    public async Task PublishAsync(
        string channelId,
        ulong sequence,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await Connection.SendDataAsync(
            new DataFrameHeader
            {
                Sequence = sequence,
                TimestampUtcTicks = DateTime.UtcNow.Ticks,
                ChannelId = channelId,
            },
            new ReadOnlySequence<byte>(payload),
            PayloadCodec.Json,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for a log record the runner forwarded that matches <paramref name="predicate"/>.</summary>
    public Task<IpcLogRecord> WaitForLogAsync(Func<IpcLogRecord, bool> predicate, CancellationToken cancellationToken)
        => WaitForAsync(logs, predicate, "log record", cancellationToken);

    /// <summary>Waits for an unsolicited control message matching <paramref name="predicate"/>.</summary>
    public Task<TMessage> WaitForNotificationAsync<TMessage>(
        Func<TMessage, bool> predicate,
        CancellationToken cancellationToken)
        where TMessage : IpcMessage
    {
        return WaitForAsync(
            notifications,
            message => message is TMessage typed && predicate(typed),
            typeof(TMessage).Name,
            cancellationToken)
            .ContinueWith(
                task => (TMessage)task.Result,
                cancellationToken,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>Waits until <paramref name="count"/> data frames have arrived, and returns them.</summary>
    public async Task<IReadOnlyList<(DataFrameHeader Header, byte[] Payload, PayloadCodec Codec)>> WaitForFramesAsync(
        int count,
        CancellationToken cancellationToken)
    {
        await WaitForAsync(frames, _ => Count(frames) >= count, $"{count} data frames", cancellationToken)
            .ConfigureAwait(false);

        lock (gate)
            return [.. frames];
    }

    /// <summary>How many data frames have arrived so far.</summary>
    public int FrameCount => Count(frames);

    private int Count<T>(List<T> items)
    {
        lock (gate)
            return items.Count;
    }

    private async Task<T> WaitForAsync<T>(
        List<T> source,
        Func<T, bool> predicate,
        string what,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        int scanned = 0;

        while (true)
        {
            T? match = default;
            bool found = false;

            lock (gate)
            {
                for (; scanned < source.Count; scanned++)
                {
                    if (!predicate(source[scanned]))
                        continue;

                    match = source[scanned++];
                    found = true;
                    break;
                }
            }

            if (found)
                return match!;

            try
            {
                await arrived.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"No {what} arrived within {Timeout}. Runner stderr:{Environment.NewLine}{StandardError}");
            }
        }
    }

    private void Record(IpcMessage message)
    {
        lock (gate)
            notifications.Add(message);

        Signal();
    }

    private void Record(IpcLogRecord record)
    {
        lock (gate)
            logs.Add(record);

        Signal();
    }

    private void Record(DataFrameHeader header, byte[] payload, PayloadCodec codec)
    {
        lock (gate)
            frames.Add((header, payload, codec));

        Signal();
    }

    // One permit per arrival, released without an upper bound: a waiter that is behind consumes the
    // backlog rather than missing it, and a waiter that arrives late rescans the list anyway.
    private void Signal() => arrived.Release();

    /// <summary>Asks the runner to shut down and waits for the process to exit.</summary>
    public async Task<int> ShutdownAsync(CancellationToken cancellationToken)
    {
        await RequestAsync<AckMessage>(new ShutdownMessage { Reason = "test complete" }, cancellationToken)
            .ConfigureAwait(false);

        await Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return Process.ExitCode;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        await Connection.DisposeAsync().ConfigureAwait(false);
        await Reading.WaitAsync(Timeout, CancellationToken.None).ConfigureAwait(false);

        Process.Dispose();
        arrived.Dispose();
    }
}
