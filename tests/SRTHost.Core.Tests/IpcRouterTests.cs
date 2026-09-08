using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SRTHost.Core.Routing;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Tests;

/// <summary>
/// The routing rules, exercised without a process in sight.
/// </summary>
/// <remarks>
/// <see cref="IRouterEndpoint"/> exists so these can be written. Everything here - subscription
/// matching, wildcard consumers, contract-version refusal, latest-value-wins drops, channel-closed
/// notices - is a decision the router makes in microseconds, and testing it through two real runner
/// processes would mean asserting on timing to observe logic. The end-to-end path has its own tests
/// in <c>SRTHost.PluginRunner.Tests</c>; these are about what the router decides, not whether the
/// pipes work.
/// </remarks>
public class IpcRouterTests
{
    private const string ChannelId = "srt/demo/values";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeliversToASubscriberOfTheProducersChannel()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint producer = new("Demo.Producer");
        FakeEndpoint consumer = new("Demo.Consumer");

        router.RegisterProducer(producer, Channel(ChannelId, "1.0"));
        router.RegisterConsumer(consumer, [Subscription(ChannelId, "1.0")]);

        Publish(router, producer, "hello", sequence: 0);

        Assert.Equal("hello", await consumer.NextPayloadAsync(Token));
    }

    /// <summary>
    /// Both registration orders have to wire the same edge. The supervisor starts producers first,
    /// but a restarted consumer arrives after its producer and a restarted producer after its
    /// consumer, so neither order can be the only one that works.
    /// </summary>
    [Fact]
    public async Task WiresRegardlessOfWhichSideRegistersFirst()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint consumer = new("Demo.Consumer");
        FakeEndpoint producer = new("Demo.Producer");

        router.RegisterConsumer(consumer, [Subscription(ChannelId, "1.0")]);
        router.RegisterProducer(producer, Channel(ChannelId, "1.0"));

        Publish(router, producer, "late", sequence: 0);

        Assert.Equal("late", await consumer.NextPayloadAsync(Token));
    }

    [Fact]
    public async Task FansOneFrameOutToEverySubscriber()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint producer = new("Demo.Producer");
        FakeEndpoint first = new("Demo.First");
        FakeEndpoint second = new("Demo.Second");

        router.RegisterProducer(producer, Channel(ChannelId, "1.0"));
        router.RegisterConsumer(first, [Subscription(ChannelId, "1.0")]);
        router.RegisterConsumer(second, [Subscription(ChannelId, "1.0")]);

        Publish(router, producer, "both", sequence: 0);

        Assert.Equal("both", await first.NextPayloadAsync(Token));
        Assert.Equal("both", await second.NextPayloadAsync(Token));
    }

    /// <summary>
    /// A wildcard subscription is how a generic consumer - a JSON writer, the web bridge - works
    /// without referencing a single payload type.
    /// </summary>
    [Fact]
    public async Task WildcardSubscriberReceivesEveryChannel()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint first = new("Demo.ProducerA");
        FakeEndpoint second = new("Demo.ProducerB");
        FakeEndpoint watcher = new("Demo.Watcher");

        router.RegisterProducer(first, Channel("srt/a", "1.0"));
        router.RegisterProducer(second, Channel("srt/b", "1.0"));
        router.RegisterConsumer(watcher, [Subscription("*", null)]);

        Publish(router, first, "from-a", sequence: 0, channelId: "srt/a");
        Publish(router, second, "from-b", sequence: 0, channelId: "srt/b");

        List<string> received = [await watcher.NextPayloadAsync(Token), await watcher.NextPayloadAsync(Token)];

        Assert.Contains("from-a", received);
        Assert.Contains("from-b", received);
    }

    [Fact]
    public async Task IgnoresAChannelNobodySubscribedTo()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint producer = new("Demo.Producer");
        FakeEndpoint consumer = new("Demo.Consumer");

        router.RegisterProducer(producer, Channel("srt/other", "1.0"));
        router.RegisterConsumer(consumer, [Subscription(ChannelId, "1.0")]);

        Publish(router, producer, "unwanted", sequence: 0, channelId: "srt/other");

        Assert.Empty(router.Edges);
        Assert.Equal(0, consumer.Delivered);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Refused up front rather than left to fail at deserialisation, where it would throw once per
    /// frame at 30 Hz with nothing saying why.
    /// </summary>
    [Fact]
    public async Task DoesNotWireAConsumerNeedingANewerContract()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint producer = new("Demo.Producer");
        FakeEndpoint consumer = new("Demo.Consumer");

        router.RegisterProducer(producer, Channel(ChannelId, "1.0"));
        router.RegisterConsumer(consumer, [Subscription(ChannelId, "2.0")]);

        Assert.Empty(router.Edges);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task WiresAConsumerHappyWithAnOlderContract()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint producer = new("Demo.Producer");
        FakeEndpoint consumer = new("Demo.Consumer");

        router.RegisterProducer(producer, Channel(ChannelId, "2.1"));
        router.RegisterConsumer(consumer, [Subscription(ChannelId, "2.0")]);

        Assert.Single(router.Edges);

        await Task.CompletedTask;
    }

    /// <summary>
    /// The promise <see cref="ChannelClosedMessage"/> was added to keep: an overlay showing the last
    /// frame of a game that has exited looks exactly like a working overlay, which is worse than a
    /// blank one.
    /// </summary>
    [Fact]
    public async Task TellsSubscribersWhenAProducerGoesAway()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint producer = new("Demo.Producer");
        FakeEndpoint consumer = new("Demo.Consumer");

        router.RegisterProducer(producer, Channel(ChannelId, "1.0"));
        router.RegisterConsumer(consumer, [Subscription(ChannelId, "1.0")]);

        await router.UnregisterAsync(producer.PluginId);

        Assert.Equal(ChannelId, Assert.Single(consumer.ClosedChannels));
        Assert.Empty(router.Edges);
    }

    /// <summary>
    /// Latest-value-wins. A consumer that cannot keep up loses frames rather than stalling the
    /// producer, and the frames it loses are the stale ones.
    /// </summary>
    [Fact]
    public async Task DropsStaleFramesForASlowConsumerRatherThanQueueingThem()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint producer = new("Demo.Producer");
        FakeEndpoint consumer = new("Demo.Slow") { Gate = new SemaphoreSlim(0) };

        router.RegisterProducer(producer, Channel(ChannelId, "1.0"));
        router.RegisterConsumer(consumer, [Subscription(ChannelId, "1.0")]);

        // Far more than the queue holds, while the consumer is blocked on its gate.
        for (int index = 0; index < 200; index++)
            Publish(router, producer, $"frame-{index}", (ulong)index);

        // Let it drain. Whatever it sees, it must not be all two hundred, and the last frame
        // published must be among them - that is what "latest value wins" means.
        consumer.Gate!.Release(200);

        string last = await consumer.WaitForPayloadAsync("frame-199", Token);

        Assert.Equal("frame-199", last);
        Assert.True(consumer.Delivered < 200, $"Expected drops, but all {consumer.Delivered} frames were delivered.");

        EdgeSnapshot edge = Assert.Single(router.Edges);
        Assert.True(edge.Dropped > 0, "Expected the edge to report drops.");
    }

    [Fact]
    public async Task RecordsDeliveryLatency()
    {
        await using IpcRouter router = NewRouter();

        FakeEndpoint producer = new("Demo.Producer");
        FakeEndpoint consumer = new("Demo.Consumer");

        router.RegisterProducer(producer, Channel(ChannelId, "1.0"));
        router.RegisterConsumer(consumer, [Subscription(ChannelId, "1.0")]);

        Publish(router, producer, "timed", sequence: 0);
        await consumer.NextPayloadAsync(Token);

        LatencySnapshot snapshot = router.DeliveryLatency.Snapshot();

        Assert.True(snapshot.Samples > 0, "Expected at least one latency sample.");
    }

    private static IpcRouter NewRouter() => new(NullLogger<IpcRouter>.Instance);

    private static ChannelDescriptor Channel(string channelId, string contractVersion)
        => new() { ChannelId = channelId, ContractVersion = contractVersion };

    private static SubscriptionDescriptor Subscription(string channelId, string? minimumVersion)
        => new() { ChannelId = channelId, MinimumContractVersion = minimumVersion };

    private static void Publish(
        IpcRouter router,
        FakeEndpoint producer,
        string payload,
        ulong sequence,
        string channelId = ChannelId)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);

        router.Publish(
            producer.PluginId,
            new DataFrameHeader
            {
                Sequence = sequence,
                TimestampUtcTicks = DateTime.UtcNow.Ticks,
                ChannelId = channelId,
            },
            new ReadOnlySequence<byte>(bytes),
            PayloadCodec.Json);
    }

    /// <summary>A consumer that records what it was given, and can be made slow on demand.</summary>
    private sealed class FakeEndpoint(string pluginId) : IRouterEndpoint
    {
        private readonly ConcurrentQueue<string> payloads = new();
        private readonly SemaphoreSlim arrived = new(0);

        public string PluginId { get; } = pluginId;

        /// <summary>Set to make delivery block, so the bounded queue can be observed dropping.</summary>
        public SemaphoreSlim? Gate { get; init; }

        public int Delivered { get; private set; }

        public List<string> ClosedChannels { get; } = [];

        public async ValueTask SendDataAsync(
            DataFrameHeader header,
            ReadOnlyMemory<byte> payload,
            PayloadCodec codec,
            CancellationToken cancellationToken)
        {
            if (Gate is not null)
                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Copied before returning, exactly as a real endpoint must: the buffer goes back to the
            // pool as soon as this completes.
            payloads.Enqueue(Encoding.UTF8.GetString(payload.Span));
            Delivered++;
            arrived.Release();
        }

        public ValueTask NotifyChannelClosedAsync(string channelId, CancellationToken cancellationToken)
        {
            ClosedChannels.Add(channelId);
            return ValueTask.CompletedTask;
        }

        public async Task<string> NextPayloadAsync(CancellationToken cancellationToken)
        {
            await arrived.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

            return payloads.TryDequeue(out string? payload)
                ? payload
                : throw new InvalidOperationException("Signalled but nothing was queued.");
        }

        public async Task<string> WaitForPayloadAsync(string expected, CancellationToken cancellationToken)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));

            while (true)
            {
                await arrived.WaitAsync(deadline.Token).ConfigureAwait(false);

                if (payloads.TryDequeue(out string? payload) && payload == expected)
                    return payload;
            }
        }
    }
}
