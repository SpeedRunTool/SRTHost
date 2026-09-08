using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Routing;

/// <summary>
/// One plugin, as far as the router is concerned: somewhere to send payloads and channel notices.
/// </summary>
/// <remarks>
/// The router talks to this rather than to a runner process, which buys two things. The routing
/// rules - subscription matching, fan-out, latest-value-wins queues, drop accounting - become
/// testable without spawning anything, so a fan-out bug is found by a unit test in milliseconds
/// rather than by watching an overlay stutter. And the direct-peering option the protocol reserves,
/// where two runners are connected to each other instead of through the star, becomes a second
/// implementation of this interface rather than a change to the router.
/// </remarks>
public interface IRouterEndpoint
{
    /// <summary>Which plugin this is.</summary>
    string PluginId { get; }

    /// <summary>Delivers one payload.</summary>
    ValueTask SendDataAsync(
        DataFrameHeader header,
        ReadOnlyMemory<byte> payload,
        PayloadCodec codec,
        CancellationToken cancellationToken);

    /// <summary>Tells a consumer that a channel it subscribes to has gone away.</summary>
    ValueTask NotifyChannelClosedAsync(string channelId, CancellationToken cancellationToken);
}
