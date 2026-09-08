using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Routing;

/// <summary>How one producer-to-consumer edge is doing.</summary>
/// <param name="ProducerId">The producer publishing the channel.</param>
/// <param name="ConsumerId">The consumer subscribed to it.</param>
/// <param name="ChannelId">The channel.</param>
/// <param name="Delivered">Frames handed to the consumer's pipe.</param>
/// <param name="Dropped">Frames discarded because the consumer was behind.</param>
/// <param name="Degraded">Whether the consumer has been behind long enough to be worth reporting.</param>
public readonly record struct EdgeSnapshot(
    string ProducerId,
    string ConsumerId,
    string ChannelId,
    long Delivered,
    long Dropped,
    bool Degraded);

/// <summary>One channel a producer is currently publishing.</summary>
/// <param name="ProducerId">The plugin publishing it.</param>
/// <param name="ChannelId">The channel id consumers bind to.</param>
/// <param name="ContractVersion">The payload contract version the producer declared.</param>
/// <param name="Codec">How payloads on the channel are encoded.</param>
/// <param name="Subscribers">How many consumers are wired to it right now.</param>
public readonly record struct ChannelSnapshot(
    string ProducerId,
    string ChannelId,
    string ContractVersion,
    PayloadCodec Codec,
    int Subscribers);

/// <summary>One payload, copied out for a watcher rather than for a consumer.</summary>
/// <remarks>
/// Deliberately a copy. The buffer the router fans out is rented and returned as soon as the last
/// subscriber has written it, so anything a watcher kept a reference to would be recycled underneath
/// it - and the data inspector's whole job is to hold a frame still while somebody reads it.
/// </remarks>
/// <param name="ProducerId">Who published it.</param>
/// <param name="ChannelId">Which channel it went out on.</param>
/// <param name="Sequence">The producer's monotonic sequence number.</param>
/// <param name="TimestampUtc">When the producer stamped it.</param>
/// <param name="Codec">How the payload is encoded.</param>
/// <param name="Payload">The payload bytes, still undecoded.</param>
public sealed record ObservedFrame(
    string ProducerId,
    string ChannelId,
    ulong Sequence,
    DateTimeOffset TimestampUtc,
    PayloadCodec Codec,
    ReadOnlyMemory<byte> Payload);

/// <summary>
/// The star: every payload goes producer runner → here → consumer runners.
/// </summary>
/// <remarks>
/// A star rather than direct peering between runners, and the reasoning is worth restating because
/// the extra hop looks like pure cost. It buys one subscription table instead of N; one place for
/// fan-out, health, drop accounting, the live data inspector and the read-only JSON feed; N
/// connections rather than N²; a crashed leg contained to one edge; and the UI gets every tick for
/// free rather than needing a separate tap. The hop itself is a copy and a pipe write - measured in
/// microseconds against a 33 ms budget.
/// <para>
/// The router never decodes a payload. It reads the small binary preamble, looks up who wants that
/// channel, and forwards the bytes untouched. That is the property that lets it stay one AnyCPU
/// process while the plugins it routes between are x86 or x64 and share no types at all.
/// </para>
/// <para>
/// Per-consumer queues are bounded at two frames and drop the oldest. For a HUD that is correct
/// behaviour rather than a failure: a frame that has been overtaken describes a game state that is
/// no longer true, and delivering it late is strictly worse than skipping it. Applying backpressure
/// instead would let one slow consumer stall the producer and, through it, every other consumer.
/// </para>
/// </remarks>
public sealed class IpcRouter : IAsyncDisposable
{
    /// <summary>
    /// Frames a consumer may be behind before the oldest is dropped.
    /// </summary>
    /// <remarks>
    /// Two, not one: one frame means a consumer that is merely mid-callback when the next tick
    /// arrives drops it, so a healthy consumer would report drops constantly. Two absorbs the
    /// ordinary overlap and still means "latest value wins" in every case that matters.
    /// </remarks>
    private const int QueueCapacity = 2;

    /// <summary>Consecutive drops before an edge is called degraded.</summary>
    private const int DegradedAfterConsecutiveDrops = 100;

    private readonly ILogger<IpcRouter> logger;
    private readonly ConcurrentDictionary<string, ProducerRegistration> producers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConsumerRegistration> consumers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();

    private int disposed;

    /// <summary>Creates a router.</summary>
    public IpcRouter(ILogger<IpcRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <summary>
    /// Time from a producer stamping a frame to the router handing it to a consumer's pipe.
    /// </summary>
    /// <remarks>
    /// Measured against <see cref="DataFrameHeader.TimestampUtcTicks"/>, which exists for this. It
    /// spans two processes, so it includes the producer's own pipe write and the router's read - in
    /// other words it is what a consumer actually experiences, not just what the router adds.
    /// <c>DateTime.UtcNow</c> is used rather than <see cref="System.Diagnostics.Stopwatch"/> because
    /// only the former is comparable across processes.
    /// </remarks>
    public LatencyStatistics DeliveryLatency { get; } = new();

    /// <summary>Time spent inside the router itself, from frame arrival to enqueue for every subscriber.</summary>
    public LatencyStatistics RoutingLatency { get; } = new();

    /// <summary>
    /// A watcher for published payloads, for the UI's data inspector. Null - and free - when nothing
    /// is watching.
    /// </summary>
    /// <remarks>
    /// A single settable delegate rather than an event, and that is the honest shape for what this
    /// is: there is exactly one inspector, it is attached while a panel is open and detached when it
    /// closes, and a multicast list would make the hot path's cost depend on how many subscribers
    /// forgot to unsubscribe.
    /// <para>
    /// The cost when nothing is watching is one null check per published frame. When something is
    /// watching it is an allocation and a copy per frame, which is why the inspector attaches only
    /// while its panel is open. The observer is called on the producer runner's read loop, so an
    /// implementation must hand off and return; anything slow here is backpressure on the producer.
    /// </para>
    /// </remarks>
    public Action<ObservedFrame>? FrameObserver { get; set; }

    /// <summary>Registers a producer and wires it to any consumer already waiting for its channel.</summary>
    public void RegisterProducer(IRouterEndpoint endpoint, ChannelDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(channel);

        ProducerRegistration registration = new(endpoint, channel);
        producers[endpoint.PluginId] = registration;

        logger.LogInformation(
            "Producer {PluginId} publishes {ChannelId} v{ContractVersion}.",
            endpoint.PluginId,
            channel.ChannelId,
            channel.ContractVersion);

        foreach (ConsumerRegistration consumer in consumers.Values)
            TryConnect(registration, consumer);
    }

    /// <summary>Registers a consumer and wires it to any producer already publishing what it wants.</summary>
    public void RegisterConsumer(IRouterEndpoint endpoint, IReadOnlyList<SubscriptionDescriptor> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(subscriptions);

        ConsumerRegistration registration = new(endpoint, subscriptions);
        consumers[endpoint.PluginId] = registration;

        logger.LogInformation(
            "Consumer {PluginId} subscribes to {Channels}.",
            endpoint.PluginId,
            string.Join(", ", subscriptions.Select(subscription => subscription.ChannelId)));

        foreach (ProducerRegistration producer in producers.Values)
            TryConnect(producer, registration);
    }

    /// <summary>
    /// Removes a plugin, tearing down every edge it was part of.
    /// </summary>
    /// <remarks>
    /// A departing producer's subscribers are told the channel closed. That is what stops an overlay
    /// from sitting there displaying the final frame of a game that has exited - the single most
    /// misleading thing a HUD can do, because it looks exactly like a working overlay.
    /// </remarks>
    public async ValueTask UnregisterAsync(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (producers.TryRemove(pluginId, out ProducerRegistration? producer))
        {
            foreach (ConsumerRegistration consumer in consumers.Values)
                await consumer.DisconnectAsync(producer, notifyClosed: true).ConfigureAwait(false);
        }

        if (consumers.TryRemove(pluginId, out ConsumerRegistration? departing))
            await departing.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Fans one published payload out to every consumer bound to its channel.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the frame is queued for each subscriber. It deliberately does not wait for
    /// the pipe writes: the producer runner is blocked on this call, and making it wait for the
    /// slowest consumer is the backpressure the bounded queues exist to avoid.
    /// </remarks>
    public void Publish(
        string producerId,
        in DataFrameHeader header,
        in ReadOnlySequence<byte> payload,
        PayloadCodec codec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerId);

        if (!producers.TryGetValue(producerId, out ProducerRegistration? producer))
            return;

        long startedTicks = DateTime.UtcNow.Ticks;

        // Before the early return below, not after: a producer with no consumers is exactly the case
        // somebody opens the inspector to diagnose, and a tap that only fired on delivered frames
        // would show nothing precisely when it is needed.
        if (FrameObserver is { } observe)
        {
            observe(new ObservedFrame(
                producerId,
                header.ChannelId,
                header.Sequence,
                new DateTimeOffset(header.TimestampUtcTicks, TimeSpan.Zero),
                codec,
                payload.ToArray()));
        }

        ConsumerEdge[] edges = producer.Edges;

        if (edges.Length == 0)
            return;

        // One rent and one copy for the whole fan-out; each edge takes a reference and releases it
        // when the frame is sent or dropped.
        PooledFrame frame = PooledFrame.Copy(header, payload, codec, edges.Length);

        foreach (ConsumerEdge edge in edges)
            edge.Enqueue(frame);

        RoutingLatency.Record(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - startedTicks));
    }

    /// <summary>Everything currently being published, whether or not anyone is listening.</summary>
    public IReadOnlyList<ChannelSnapshot> Channels =>
    [
        .. producers.Values.Select(producer => new ChannelSnapshot(
            producer.Endpoint.PluginId,
            producer.Channel.ChannelId,
            producer.Channel.ContractVersion,
            producer.Channel.Codec,
            producer.Edges.Length)),
    ];

    /// <summary>Everything currently known about the wired-up edges.</summary>
    public IReadOnlyList<EdgeSnapshot> Edges =>
    [
        .. producers.Values
            .SelectMany(producer => producer.Edges)
            .Select(edge => edge.Snapshot()),
    ];

    private void TryConnect(ProducerRegistration producer, ConsumerRegistration consumer)
    {
        foreach (SubscriptionDescriptor subscription in consumer.Subscriptions)
        {
            if (!Matches(subscription, producer.Channel))
                continue;

            ConsumerEdge edge = new(
                producer.Endpoint.PluginId,
                consumer.Endpoint,
                producer.Channel.ChannelId,
                DeliveryLatency,
                logger,
                lifetime.Token);

            if (!producer.TryAddEdge(edge) || !consumer.TryAddEdge(producer.Endpoint.PluginId, edge))
            {
                // Already wired - both sides register on startup and the second one to arrive walks
                // the other's list, so a pair can legitimately be considered twice.
                edge.Dispose();
                return;
            }

            logger.LogInformation(
                "Wired {ProducerId} -> {ConsumerId} on {ChannelId}.",
                producer.Endpoint.PluginId,
                consumer.Endpoint.PluginId,
                producer.Channel.ChannelId);

            return;
        }
    }

    /// <summary>
    /// Whether a subscription accepts what a producer publishes.
    /// </summary>
    /// <remarks>
    /// <c>*</c> matches everything, which is how a generic consumer - a JSON writer, the web bridge -
    /// works without referencing a single payload type.
    /// <para>
    /// The version check is refused up front rather than left to fail at deserialisation. A consumer
    /// declaring it needs 2.0 and being handed 1.0 would otherwise throw once per frame, thirty
    /// times a second, with nothing saying why.
    /// </para>
    /// </remarks>
    private bool Matches(SubscriptionDescriptor subscription, ChannelDescriptor channel)
    {
        if (subscription.ChannelId != "*"
            && !string.Equals(subscription.ChannelId, channel.ChannelId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (subscription.MinimumContractVersion is null)
            return true;

        if (!Version.TryParse(subscription.MinimumContractVersion, out Version? minimum)
            || !Version.TryParse(channel.ContractVersion, out Version? published))
        {
            // An unparseable version on either side is allowed through rather than silently
            // dropping the edge. The alternative is a plugin that never receives anything and gives
            // no indication why; a decode failure downstream at least names the payload.
            logger.LogWarning(
                "Could not compare contract versions '{Minimum}' and '{Published}' for {ChannelId}; wiring anyway.",
                subscription.MinimumContractVersion,
                channel.ContractVersion,
                channel.ChannelId);

            return true;
        }

        if (published >= minimum)
            return true;

        logger.LogWarning(
            "Not wiring {ChannelId}: the producer publishes v{Published} and the consumer needs at least v{Minimum}.",
            channel.ChannelId,
            published,
            minimum);

        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await lifetime.CancelAsync().ConfigureAwait(false);

        foreach (ConsumerRegistration consumer in consumers.Values)
            await consumer.DisposeAsync().ConfigureAwait(false);

        producers.Clear();
        consumers.Clear();
        lifetime.Dispose();
    }

    private sealed class ProducerRegistration(IRouterEndpoint endpoint, ChannelDescriptor channel)
    {
        private readonly Lock gate = new();
        private ConsumerEdge[] edges = [];

        public IRouterEndpoint Endpoint { get; } = endpoint;

        public ChannelDescriptor Channel { get; } = channel;

        /// <summary>
        /// The current edge set, as an array that <c>Publish</c> can walk without a lock.
        /// </summary>
        /// <remarks>
        /// Copy-on-write: wiring changes a handful of times per session, delivery happens thirty
        /// times a second per producer, so the cost belongs on the rare side. A publisher may
        /// briefly use the previous array, which means one frame delivered on an edge that has just
        /// been removed - harmless, and far cheaper than a lock on the hot path.
        /// </remarks>
        public ConsumerEdge[] Edges => Volatile.Read(ref edges);

        public bool TryAddEdge(ConsumerEdge edge)
        {
            lock (gate)
            {
                if (edges.Any(existing => existing.ConsumerId == edge.ConsumerId))
                    return false;

                Volatile.Write(ref edges, [.. edges, edge]);
                return true;
            }
        }

        public void RemoveEdge(ConsumerEdge edge)
        {
            lock (gate)
                Volatile.Write(ref edges, [.. edges.Where(existing => existing != edge)]);
        }
    }

    private sealed class ConsumerRegistration(IRouterEndpoint endpoint, IReadOnlyList<SubscriptionDescriptor> subscriptions)
        : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, ConsumerEdge> byProducer = new(StringComparer.OrdinalIgnoreCase);

        public IRouterEndpoint Endpoint { get; } = endpoint;

        public IReadOnlyList<SubscriptionDescriptor> Subscriptions { get; } = subscriptions;

        public bool TryAddEdge(string producerId, ConsumerEdge edge) => byProducer.TryAdd(producerId, edge);

        public async ValueTask DisconnectAsync(ProducerRegistration producer, bool notifyClosed)
        {
            if (!byProducer.TryRemove(producer.Endpoint.PluginId, out ConsumerEdge? edge))
                return;

            producer.RemoveEdge(edge);
            edge.Dispose();

            if (!notifyClosed)
                return;

            try
            {
                await Endpoint.NotifyChannelClosedAsync(producer.Channel.ChannelId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The consumer may be going away in the same breath - a host shutdown takes both
                // runners down at once - and failing to tell a dead process its channel closed is
                // not a problem worth propagating.
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (ConsumerEdge edge in byProducer.Values)
                edge.Dispose();

            byProducer.Clear();

            await ValueTask.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>One producer-to-consumer edge: a bounded queue and the task that drains it.</summary>
    private sealed class ConsumerEdge : IDisposable
    {
        private readonly Channel<PooledFrame> queue;
        private readonly IRouterEndpoint consumer;
        private readonly LatencyStatistics deliveryLatency;
        private readonly ILogger logger;
        private readonly CancellationTokenSource lifetime;
        private readonly Task pump;

        private long delivered;
        private long dropped;
        private int consecutiveDrops;
        private bool degraded;

        public ConsumerEdge(
            string producerId,
            IRouterEndpoint consumer,
            string channelId,
            LatencyStatistics deliveryLatency,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            ProducerId = producerId;
            ChannelId = channelId;

            this.consumer = consumer;
            this.deliveryLatency = deliveryLatency;
            this.logger = logger;

            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            queue = Channel.CreateBounded<PooledFrame>(
                new BoundedChannelOptions(QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                },
                // Dropped frames still hold a reference on their pooled buffer. Without this callback
                // every drop would leak a rented array - and dropping is what this queue is for.
                OnDropped);

            pump = Task.Run(() => PumpAsync(lifetime.Token), CancellationToken.None);
        }

        public string ProducerId { get; }

        public string ConsumerId => consumer.PluginId;

        public string ChannelId { get; }

        public void Enqueue(PooledFrame frame)
        {
            if (queue.Writer.TryWrite(frame))
                return;

            // Only reachable once the queue is completed, i.e. the edge is being torn down.
            frame.Release();
        }

        public EdgeSnapshot Snapshot() => new(
            ProducerId,
            ConsumerId,
            ChannelId,
            Interlocked.Read(ref delivered),
            Interlocked.Read(ref dropped),
            Volatile.Read(ref degraded));

        private void OnDropped(PooledFrame frame)
        {
            frame.Release();

            Interlocked.Increment(ref dropped);

            if (Interlocked.Increment(ref consecutiveDrops) < DegradedAfterConsecutiveDrops || degraded)
                return;

            // A hundred in a row is not a slow frame, it is a consumer that has stopped keeping up
            // at all - worth surfacing, whereas the occasional drop is the design working.
            Volatile.Write(ref degraded, true);

            logger.LogWarning(
                "{ConsumerId} has dropped {Count} consecutive frames from {ProducerId} on {ChannelId}.",
                ConsumerId,
                DegradedAfterConsecutiveDrops,
                ProducerId,
                ChannelId);
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (PooledFrame frame in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await consumer
                            .SendDataAsync(frame.Header, frame.Payload, frame.Codec, cancellationToken)
                            .ConfigureAwait(false);

                        Interlocked.Increment(ref delivered);
                        Interlocked.Exchange(ref consecutiveDrops, 0);
                        Volatile.Write(ref degraded, false);

                        // Recorded here rather than at enqueue, because this is the point at which
                        // the consumer can actually see the frame - the queue wait is part of what
                        // the star costs and hiding it would make the number flattering and useless.
                        deliveryLatency.Record(
                            TimeSpan.FromTicks(DateTime.UtcNow.Ticks - frame.Header.TimestampUtcTicks));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The consumer's pipe is gone. The supervisor owns what happens next; this
                        // edge just stops, rather than logging once per frame about it.
                        logger.LogDebug(ex, "Delivery to {ConsumerId} failed; the edge is stopping.", ConsumerId);
                        return;
                    }
                    finally
                    {
                        frame.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Torn down.
            }
            finally
            {
                // Whatever is left in the queue still holds pooled buffers.
                while (queue.Reader.TryRead(out PooledFrame? orphan))
                    orphan.Release();
            }
        }

        public void Dispose()
        {
            queue.Writer.TryComplete();
            lifetime.Cancel();

            // Not awaited: Dispose is called from registration paths that must not block, and the
            // pump's only remaining job is to release pooled buffers, which it does on the way out.
            _ = pump;

            lifetime.Dispose();
        }
    }
}
