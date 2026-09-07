using System.Buffers;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Ipc.Tests;

/// <summary>
/// Drives <see cref="IpcConnection"/> over real named pipes, in the same configuration the router
/// and a runner use.
/// </summary>
/// <remarks>
/// Deliberately not over an in-memory duplex stream. The things most likely to be wrong here -
/// <c>CurrentUserOnly</c> and <c>FirstPipeInstance</c> being accepted together, a byte-mode pipe
/// fragmenting frames differently from a <see cref="System.IO.Pipelines.Pipe"/>, connect ordering
/// between two ends that start independently - are precisely the things an in-memory substitute
/// would paper over.
/// </remarks>
public class IpcConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RequestGetsItsOwnCorrelatedResponse()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            onServerMessage: (message, _, _) => ValueTask.FromResult<IpcMessage?>(
                message is PingMessage ? new AckMessage() : null));

        IpcMessage reply = await fixture.Client.RequestAsync(
            new PingMessage(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<AckMessage>(reply);
    }

    /// <summary>
    /// Replies are matched by correlation id, not by arrival order, so answering out of order must
    /// still land each reply on the right caller.
    /// </summary>
    [Fact]
    public async Task MatchesConcurrentRequestsByCorrelationIdNotArrivalOrder()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            onServerMessage: async (message, _, _) =>
            {
                // Invert the ordering: a low interval waits longer than a high one, so the replies
                // come back in the opposite order to the requests.
                SetProduceIntervalMessage interval = Assert.IsType<SetProduceIntervalMessage>(message);
                await Task.Delay(50 - interval.IntervalMilliseconds);
                return new ConfigurationSchemaMessage
                {
                    SchemaJson = interval.IntervalMilliseconds.ToString(),
                    CurrentJson = "{}",
                };
            });

        Task<IpcMessage>[] requests = [.. Enumerable.Range(1, 20).Select(i =>
            fixture.Client.RequestAsync(
                new SetProduceIntervalMessage { IntervalMilliseconds = i },
                cancellationToken: TestContext.Current.CancellationToken))];

        IpcMessage[] replies = await Task.WhenAll(requests).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        for (int i = 0; i < replies.Length; i++)
            Assert.Equal((i + 1).ToString(), Assert.IsType<ConfigurationSchemaMessage>(replies[i]).SchemaJson);
    }

    [Fact]
    public async Task AnErrorResponseSurfacesAsAnException()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            onServerMessage: (_, _, _) => ValueTask.FromResult<IpcMessage?>(new ErrorMessage
            {
                Message = "the plugin refused to load",
                SubStatus = PluginSubStatus.IncorrectArchitecture,
                Detail = "x86 plugin in the x64 runner",
            }));

        IpcRequestException error = await Assert.ThrowsAsync<IpcRequestException>(() =>
            fixture.Client.RequestAsync(new StartPluginMessage(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("the plugin refused to load", error.Message);
        Assert.Equal(PluginSubStatus.IncorrectArchitecture, error.SubStatus);
    }

    [Fact]
    public async Task DataFramesCarryTheirHeaderAndPayloadIntact()
    {
        TaskCompletionSource<(DataFrameHeader Header, byte[] Payload)> received = new();

        await using Fixture fixture = await Fixture.CreateAsync(
            onServerData: (header, payload, _, _) =>
            {
                received.TrySetResult((header, payload.ToArray()));
                return ValueTask.CompletedTask;
            });

        byte[] payload = Encoding.UTF8.GetBytes("""{"tick":42}""");

        await fixture.Client.SendDataAsync(
            new DataFrameHeader
            {
                Sequence = 99,
                TimestampUtcTicks = 638_000_000_000_000_000,
                ChannelId = "srt/demo/values",
            },
            new ReadOnlySequence<byte>(payload),
            cancellationToken: TestContext.Current.CancellationToken);

        (DataFrameHeader header, byte[] body) = await received.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(99ul, header.Sequence);
        Assert.Equal(638_000_000_000_000_000, header.TimestampUtcTicks);
        Assert.Equal("srt/demo/values", header.ChannelId);
        Assert.Equal(payload, body);
    }

    [Fact]
    public async Task LogRecordsCrossTheBoundary()
    {
        TaskCompletionSource<IpcLogRecord> received = new();

        await using Fixture fixture = await Fixture.CreateAsync(
            onServerLog: (record, _) =>
            {
                received.TrySetResult(record);
                return ValueTask.CompletedTask;
            });

        await fixture.Client.SendLogAsync(
            new IpcLogRecord
            {
                Timestamp = DateTimeOffset.UnixEpoch,
                Level = LogLevel.Warning,
                Category = "SpeedRunTool.Demo.Producer",
                Message = "the source went away",
                EventId = 1234,
            },
            TestContext.Current.CancellationToken);

        IpcLogRecord record = await received.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Equal("SpeedRunTool.Demo.Producer", record.Category);
        Assert.Equal("the source went away", record.Message);
        Assert.Equal(1234, record.EventId);
    }

    /// <summary>
    /// A runner that dies leaves every in-flight request waiting on a reply that will never come.
    /// Failing them at disconnect rather than letting each hit its own timeout turns a 30 second
    /// stall at every call site into an immediate, accurate error.
    /// </summary>
    [Fact]
    public async Task PendingRequestsFailImmediatelyWhenThePeerDisconnects()
    {
        Fixture fixture = await Fixture.CreateAsync(
            onServerMessage: (_, _, _) => ValueTask.FromResult<IpcMessage?>(null));

        Task<IpcMessage> request = fixture.Client.RequestAsync(
            new PingMessage(),
            cancellationToken: TestContext.Current.CancellationToken);

        await fixture.DisposeServerAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => request.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        await fixture.DisposeAsync();
    }

    /// <summary>
    /// A pre-squatted pipe name must be a hard failure. Without FirstPipeInstance, Windows hands back
    /// another instance of somebody else's pipe and the router would serve a runner's control channel
    /// to whoever got there first.
    /// </summary>
    [Fact]
    public void ASecondServerCannotTakeAPipeNameAlreadyInUse()
    {
        string name = IpcProtocol.PipeName(IpcProtocol.NewSessionId(), "SpeedRunTool.Demo.Producer");

        using NamedPipeServerStream first = IpcPipe.CreateServer(name);

        Assert.Throws<IOException>(() => IpcPipe.CreateServer(name));
    }

    [Fact]
    public async Task ConnectingToANameNobodyIsServingTimesOut()
    {
        string name = IpcProtocol.PipeName(IpcProtocol.NewSessionId(), "SpeedRunTool.Demo.Absent");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            IpcPipe.ConnectAsync(name, TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NotificationsArriveWithoutACorrelationId()
    {
        TaskCompletionSource<uint> correlation = new();

        await using Fixture fixture = await Fixture.CreateAsync(
            onServerMessage: (_, correlationId, _) =>
            {
                correlation.TrySetResult(correlationId);
                return ValueTask.FromResult<IpcMessage?>(null);
            });

        await fixture.Client.SendAsync(
            new HeartbeatMessage { UptimeMilliseconds = 1000 },
            TestContext.Current.CancellationToken);

        Assert.Equal(0u, await correlation.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    /// <summary>A connected router/runner pair over a real pipe, plus their read loops.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private IpcConnection? server;
        private Task? serverLoop;
        private Task? clientLoop;

        public required IpcConnection Client { get; init; }

        public static async Task<Fixture> CreateAsync(
            ControlMessageHandler? onServerMessage = null,
            DataFrameHandler? onServerData = null,
            LogRecordHandler? onServerLog = null)
        {
            string name = IpcProtocol.PipeName(IpcProtocol.NewSessionId(), "SpeedRunTool.Demo.Producer");

            NamedPipeServerStream serverPipe = IpcPipe.CreateServer(name);
            Task waiting = serverPipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            NamedPipeClientStream clientPipe = await IpcPipe.ConnectAsync(name, Timeout, TestContext.Current.CancellationToken);
            await waiting.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            IpcConnection server = new(serverPipe)
            {
                OnControlMessage = onServerMessage,
                OnDataFrame = onServerData,
                OnLogRecord = onServerLog,
            };

            IpcConnection client = new(clientPipe);

            Fixture fixture = new()
            {
                Client = client,
                server = server,
                serverLoop = Task.Run(() => Swallow(server.RunAsync(CancellationToken.None))),
                clientLoop = Task.Run(() => Swallow(client.RunAsync(CancellationToken.None))),
            };

            return fixture;
        }

        public async Task DisposeServerAsync()
        {
            if (server is not null)
            {
                await server.DisposeAsync();
                server = null;
            }

            if (serverLoop is not null)
                await serverLoop;
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeServerAsync();
            await Client.DisposeAsync();

            if (clientLoop is not null)
                await clientLoop;
        }

        /// <summary>
        /// A read loop always ends in an exception - the peer disconnects, or the connection is
        /// disposed - and that is the normal path, not a failure the tests should surface.
        /// </summary>
        private static async Task Swallow(Task loop)
        {
            try
            {
                await loop;
            }
            catch
            {
                // Expected at teardown.
            }
        }
    }
}
