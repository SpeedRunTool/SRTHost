using System.Text.Json;
using SRTHost.Ipc;
using SpeedRunTool.Demo.Contracts;
using SRTPluginBase.Abstractions;

namespace SRTHost.PluginRunner.Tests;

/// <summary>
/// Drives a real runner process end to end: handshake, load, start, publish, configure, stop, exit.
/// </summary>
public class RunnerEndToEndTests
{
    private const string ProducerId = "SpeedRunTool.Demo.Producer";
    private const string ProducerType = "SpeedRunTool.Demo.Producer.DemoProducer";
    private const string ConsumerId = "SpeedRunTool.Demo.Consumer";
    private const string ConsumerType = "SpeedRunTool.Demo.Consumer.DemoConsumer";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HandshakeReportsTheRunnersArchitectureAndGeneration()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        Assert.Equal(IpcProtocol.Version, harness.HelloAck.ProtocolVersion);
        Assert.Equal(SrtContract.Generation, harness.HelloAck.ContractGeneration);
        Assert.NotEqual(PluginArchitecture.Any, harness.HelloAck.Architecture);
        Assert.Equal(harness.Process.Id, harness.HelloAck.ProcessId);
        Assert.NotEmpty(harness.HelloAck.RuntimeVersion);
    }

    /// <summary>
    /// A protocol mismatch is refused rather than negotiated, and refused without killing the
    /// connection - the router has to be able to read the reason it was turned away.
    /// </summary>
    [Fact]
    public async Task RefusesAProtocolVersionItDoesNotSpeak()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        IpcRequestException failure = await Assert.ThrowsAsync<IpcRequestException>(
            async () => await harness.RequestAsync<HelloAckMessage>(
                new HelloMessage
                {
                    ProtocolVersion = IpcProtocol.Version + 1,
                    ContractGeneration = SrtContract.Generation,
                    SessionId = "deadbeef",
                },
                Token));

        Assert.Contains("protocol", failure.Message, StringComparison.OrdinalIgnoreCase);

        // Still alive and still answering, which is the half of this that matters.
        await harness.RequestAsync<AckMessage>(new PingMessage(), Token);
    }

    [Fact]
    public async Task LoadsAProducerOutOfAPluginFolderAndDeclaresItsChannel()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        ReadyMessage ready = await harness.LoadAsync(ProducerId, ProducerType, Token);

        Assert.Equal(PluginKind.Producer, ready.PluginKind);
        Assert.NotNull(ready.Channel);
        Assert.Equal(DemoChannel.Id, ready.Channel.ChannelId);
        Assert.Equal("1.0", ready.Channel.ContractVersion);
        Assert.Empty(ready.Subscriptions);
        Assert.False(ready.RequiresUiThread);
    }

    [Fact]
    public async Task PublishesPayloadsWhileStartedAndStopsWhenStopped()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await harness.LoadAsync(ProducerId, ProducerType, Token);
        await harness.RequestAsync<AckMessage>(
            new SetProduceIntervalMessage { IntervalMilliseconds = 10 },
            Token);
        await harness.RequestAsync<AckMessage>(new StartPluginMessage(), Token);

        IReadOnlyList<(DataFrameHeader Header, byte[] Payload, PayloadCodec Codec)> frames =
            await harness.WaitForFramesAsync(5, Token);

        // Sequence numbers are per channel and monotonic, which is what lets a consumer notice a
        // frame it never received.
        Assert.Equal(0ul, frames[0].Header.Sequence);

        for (int index = 1; index < frames.Count; index++)
            Assert.Equal(frames[index - 1].Header.Sequence + 1, frames[index].Header.Sequence);

        foreach ((DataFrameHeader header, byte[] payload, PayloadCodec codec) in frames.Take(5))
        {
            Assert.Equal(DemoChannel.Id, header.ChannelId);
            Assert.Equal(PayloadCodec.Json, codec);

            DemoPayload? decoded = JsonSerializer.Deserialize(payload, DemoPayloadJsonContext.Default.DemoPayload);

            Assert.NotNull(decoded);
            Assert.True(decoded.Tick > 0);
        }

        await harness.RequestAsync<AckMessage>(new StopPluginMessage(), Token);

        int afterStop = harness.FrameCount;
        await Task.Delay(TimeSpan.FromMilliseconds(250), Token);

        Assert.Equal(afterStop, harness.FrameCount);
    }

    /// <summary>
    /// The producer's lifecycle is reported as it happens, because the plugin list has to show
    /// something other than "loaded" while a plugin is starting.
    /// </summary>
    [Fact]
    public async Task ReportsItsLifecycleTransitions()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await harness.LoadAsync(ProducerId, ProducerType, Token);
        await harness.WaitForNotificationAsync<PluginStatusChangedMessage>(
            message => message.Status == PluginStatus.Loaded,
            Token);

        await harness.RequestAsync<AckMessage>(new StartPluginMessage(), Token);
        await harness.WaitForNotificationAsync<PluginStatusChangedMessage>(
            message => message.Status == PluginStatus.Running,
            Token);

        await harness.RequestAsync<AckMessage>(new StopPluginMessage(), Token);
        await harness.WaitForNotificationAsync<PluginStatusChangedMessage>(
            message => message.Status == PluginStatus.Stopped,
            Token);
    }

    [Fact]
    public async Task SendsHeartbeats()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        HeartbeatMessage heartbeat = await harness.WaitForNotificationAsync<HeartbeatMessage>(_ => true, Token);

        Assert.True(heartbeat.UptimeMilliseconds >= 0);
    }

    /// <summary>
    /// Out-of-range intervals are clamped rather than refused, and the clamp is logged - a setting
    /// that silently does something other than what it says is worse than one that complains.
    /// </summary>
    [Fact]
    public async Task ClampsAnOutOfRangeProduceInterval()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await harness.RequestAsync<AckMessage>(
            new SetProduceIntervalMessage { IntervalMilliseconds = 1 },
            Token);

        IpcLogRecord warning = await harness.WaitForLogAsync(
            record => record.Message.Contains("Produce interval", StringComparison.Ordinal),
            Token);

        Assert.Contains("8 ms", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliversPayloadsToAConsumer()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        // The demo consumer logs one payload in every LogEvery, which defaults to 30 - a producer at
        // 30 Hz is otherwise a line a frame. This test publishes exactly one payload, so it turns
        // that down, which incidentally exercises settings arriving with LoadPlugin.
        ReadyMessage ready = await harness.LoadAsync(
            ConsumerId, ConsumerType, Token, configurationJson: """{"LogEvery":1}""");

        Assert.Equal(PluginKind.Consumer, ready.PluginKind);
        Assert.Null(ready.Channel);
        Assert.Equal(DemoChannel.Id, Assert.Single(ready.Subscriptions).ChannelId);

        await harness.RequestAsync<AckMessage>(new StartPluginMessage(), Token);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new DemoPayload { Tick = 42, Value = 7, Ratio = 0.5f, Label = "harness" },
            DemoPayloadJsonContext.Default.DemoPayload);

        await harness.PublishAsync(DemoChannel.Id, sequence: 0, payload, Token);

        // The demo consumer logs what it receives, so its log record is the observable proof that a
        // payload crossed the pipe, was decoded, and reached the plugin's own code.
        IpcLogRecord record = await harness.WaitForLogAsync(
            entry => entry.Message.Contains("tick=42", StringComparison.Ordinal),
            Token);

        Assert.Contains("label=harness", record.Message, StringComparison.Ordinal);
        Assert.Equal(ConsumerId, record.Category);
    }

    [Fact]
    public async Task TellsAConsumerWhenItsChannelGoesAway()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await harness.LoadAsync(ConsumerId, ConsumerType, Token);
        await harness.RequestAsync<AckMessage>(new StartPluginMessage(), Token);

        await harness.RequestAsync<AckMessage>(
            new ChannelClosedMessage { ChannelId = DemoChannel.Id },
            Token);

        await harness.WaitForLogAsync(
            entry => entry.Message.Contains($"Channel {DemoChannel.Id} closed", StringComparison.Ordinal),
            Token);
    }

    [Fact]
    public async Task ReportsAMissingEntryTypeWithoutDying()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        IpcRequestException failure = await Assert.ThrowsAsync<IpcRequestException>(
            async () => await harness.LoadAsync(ProducerId, "SpeedRunTool.Demo.Producer.NoSuchType", Token));

        Assert.Equal(PluginSubStatus.UndefinedException, failure.SubStatus);
        Assert.Contains("NoSuchType", failure.Message, StringComparison.Ordinal);

        await harness.RequestAsync<AckMessage>(new PingMessage(), Token);
    }

    [Fact]
    public async Task ReportsAMissingPluginAssembly()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        IpcRequestException failure = await Assert.ThrowsAsync<IpcRequestException>(
            async () => await harness.RequestAsync<ReadyMessage>(
                new LoadPluginMessage
                {
                    PluginId = "SpeedRunTool.Demo.Missing",
                    PluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins", "SpeedRunTool.Demo.Missing"),
                    EntryAssembly = "SpeedRunTool.Demo.Missing.dll",
                    EntryType = "SpeedRunTool.Demo.Missing.Nothing",
                },
                Token));

        Assert.Equal(PluginSubStatus.DependencyNotFound, failure.SubStatus);
    }

    /// <summary>
    /// A runner hosts exactly one plugin. Loading a second is refused rather than quietly replacing
    /// the first, which would leave the router's idea of what this process holds silently wrong.
    /// </summary>
    [Fact]
    public async Task RefusesASecondPlugin()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await harness.LoadAsync(ProducerId, ProducerType, Token);

        await Assert.ThrowsAsync<IpcRequestException>(
            async () => await harness.LoadAsync(ConsumerId, ConsumerType, Token));
    }

    [Fact]
    public async Task ExitsCleanlyOnShutdown()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await harness.LoadAsync(ProducerId, ProducerType, Token);
        await harness.RequestAsync<AckMessage>(new StartPluginMessage(), Token);
        await harness.WaitForFramesAsync(1, Token);

        Assert.Equal(0, await harness.ShutdownAsync(Token));
    }

    [Fact]
    public async Task ExitsWhenTheRouterDisappears()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await harness.LoadAsync(ProducerId, ProducerType, Token);
        await harness.RequestAsync<AckMessage>(new StartPluginMessage(), Token);
        await harness.WaitForFramesAsync(1, Token);

        // Dropping the pipe is what a router crash looks like from here. The runner must not survive
        // it: an orphan holding a game handle is the failure mode the job object exists to prevent,
        // and this is the half of that the runner itself is responsible for.
        await harness.Connection.DisposeAsync();

        await harness.Process.WaitForExitAsync(Token);
    }
}
