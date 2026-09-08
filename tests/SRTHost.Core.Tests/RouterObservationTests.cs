using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SRTHost.Core.Routing;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Tests;

/// <summary>
/// What the router exposes to the shell: the channel list, and the frame tap behind the data
/// inspector.
/// </summary>
/// <remarks>
/// Both are new in Phase 5 and both sit on the hot path, so the properties worth pinning are the
/// ones that would otherwise be discovered the expensive way: a tap that only fires when somebody is
/// already subscribed would show nothing in exactly the case it is opened to diagnose, and a tap
/// that handed out the pooled buffer would hand out memory that is recycled a microsecond later.
/// </remarks>
public class RouterObservationTests
{
    private const string ChannelId = "srt/demo/values";

    [Fact]
    public async Task ListsEveryChannelBeingPublished()
    {
        await using IpcRouter router = new(NullLogger<IpcRouter>.Instance);

        SilentEndpoint producer = new("Demo.Producer");
        SilentEndpoint consumer = new("Demo.Consumer");

        router.RegisterProducer(producer, new ChannelDescriptor
        {
            ChannelId = ChannelId,
            ContractVersion = "1.2",
            Codec = PayloadCodec.Json,
        });

        Assert.Equal(0, Assert.Single(router.Channels).Subscribers);

        router.RegisterConsumer(consumer, [new SubscriptionDescriptor { ChannelId = ChannelId }]);

        ChannelSnapshot channel = Assert.Single(router.Channels);

        Assert.Equal(ChannelId, channel.ChannelId);
        Assert.Equal("Demo.Producer", channel.ProducerId);
        Assert.Equal("1.2", channel.ContractVersion);
        Assert.Equal(1, channel.Subscribers);
    }

    /// <summary>
    /// The tap has to fire for a producer nobody is subscribed to.
    /// </summary>
    /// <remarks>
    /// This is the whole point of where the call sits in <c>Publish</c>. "My producer is running but
    /// my overlay shows nothing" is answered by watching the producer publish into the void, and a
    /// tap placed after the no-subscribers early return would show an empty panel instead.
    /// </remarks>
    [Fact]
    public async Task ObservesFramesWithNoSubscribers()
    {
        await using IpcRouter router = new(NullLogger<IpcRouter>.Instance);

        SilentEndpoint producer = new("Demo.Producer");

        router.RegisterProducer(producer, new ChannelDescriptor { ChannelId = ChannelId, ContractVersion = "1.0" });

        List<ObservedFrame> seen = [];
        router.FrameObserver = seen.Add;

        Publish(router, producer, "alone", sequence: 7);

        ObservedFrame frame = Assert.Single(seen);

        Assert.Equal("Demo.Producer", frame.ProducerId);
        Assert.Equal(ChannelId, frame.ChannelId);
        Assert.Equal(7ul, frame.Sequence);
        Assert.Equal(PayloadCodec.Json, frame.Codec);
        Assert.Equal("alone", Encoding.UTF8.GetString(frame.Payload.Span));
    }

    /// <summary>
    /// An observed payload must survive the frame it came from being recycled.
    /// </summary>
    /// <remarks>
    /// The router rents its fan-out buffer and returns it once the last subscriber has been written,
    /// so an observer handed a slice of it would be reading somebody else's data by the time a person
    /// looked at the panel. Publishing again and re-checking the first payload is how that would show
    /// up.
    /// </remarks>
    [Fact]
    public async Task ObservedPayloadsAreCopies()
    {
        await using IpcRouter router = new(NullLogger<IpcRouter>.Instance);

        SilentEndpoint producer = new("Demo.Producer");
        SilentEndpoint consumer = new("Demo.Consumer");

        router.RegisterProducer(producer, new ChannelDescriptor { ChannelId = ChannelId, ContractVersion = "1.0" });
        router.RegisterConsumer(consumer, [new SubscriptionDescriptor { ChannelId = ChannelId }]);

        List<ObservedFrame> seen = [];
        router.FrameObserver = seen.Add;

        Publish(router, producer, "first", sequence: 0);
        Publish(router, producer, "second", sequence: 1);
        Publish(router, producer, "third", sequence: 2);

        Assert.Equal(
            ["first", "second", "third"],
            seen.Select(frame => Encoding.UTF8.GetString(frame.Payload.Span)));
    }

    [Fact]
    public async Task DetachingTheObserverStopsIt()
    {
        await using IpcRouter router = new(NullLogger<IpcRouter>.Instance);

        SilentEndpoint producer = new("Demo.Producer");

        router.RegisterProducer(producer, new ChannelDescriptor { ChannelId = ChannelId, ContractVersion = "1.0" });

        int seen = 0;
        router.FrameObserver = _ => seen++;

        Publish(router, producer, "watched", sequence: 0);

        router.FrameObserver = null;

        Publish(router, producer, "unwatched", sequence: 1);

        Assert.Equal(1, seen);
    }

    private static void Publish(IpcRouter router, SilentEndpoint producer, string payload, ulong sequence)
        => router.Publish(
            producer.PluginId,
            new DataFrameHeader
            {
                Sequence = sequence,
                TimestampUtcTicks = DateTime.UtcNow.Ticks,
                ChannelId = ChannelId,
            },
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(payload)),
            PayloadCodec.Json);

    /// <summary>An endpoint that accepts everything and remembers nothing.</summary>
    private sealed class SilentEndpoint(string pluginId) : IRouterEndpoint
    {
        public string PluginId { get; } = pluginId;

        public ValueTask SendDataAsync(
            DataFrameHeader header,
            ReadOnlyMemory<byte> payload,
            PayloadCodec codec,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask NotifyChannelClosedAsync(string channelId, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
