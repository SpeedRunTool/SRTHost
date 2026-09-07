using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using SRTPluginBase.Abstractions;
using SpeedRunTool.Demo.Contracts;

[assembly: SrtPluginAssembly("SpeedRunTool.Demo.Consumer", typeof(SpeedRunTool.Demo.Consumer.DemoConsumer))]

namespace SpeedRunTool.Demo.Consumer;

/// <summary>
/// Logs whatever arrives on <see cref="DemoChannel.Id"/>.
/// </summary>
public sealed class DemoConsumer : ConsumerPluginBase<DemoPayload>
{
    /// <inheritdoc />
    public override IPluginInfo Info { get; } =
        PluginInfo.FromAssembly(typeof(DemoConsumer).Assembly);

    /// <inheritdoc />
    public override IReadOnlyList<ChannelSubscription> Subscriptions { get; } =
    [
        new ChannelSubscription
        {
            ChannelId = DemoChannel.Id,
            MinimumContractVersion = DemoChannel.ContractVersion,
        },
    ];

    /// <inheritdoc />
    protected override JsonTypeInfo<DemoPayload> PayloadTypeInfo => DemoPayloadJsonContext.Default.DemoPayload;

    /// <inheritdoc />
    protected override ValueTask OnPayloadAsync(DemoPayload payload, CancellationToken cancellationToken)
    {
        Logger.LogInformation(
            "Demo payload: tick={Tick} value={Value} ratio={Ratio:F2} label={Label}",
            payload.Tick, payload.Value, payload.Ratio, payload.Label);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask OnChannelClosedAsync(string channelId, CancellationToken cancellationToken)
    {
        // A real overlay clears its display here. Showing the last frame of a game that has exited
        // is worse than showing nothing.
        Logger.LogInformation("Channel {ChannelId} closed.", channelId);
        return ValueTask.CompletedTask;
    }
}
