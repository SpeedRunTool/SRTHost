using System.Text.Json.Serialization.Metadata;
using SRTPluginBase;
using SRTPluginBase.Abstractions;
using SpeedRunTool.Demo.Contracts;

[assembly: SrtPluginAssembly("SpeedRunTool.Demo.Producer", typeof(SpeedRunTool.Demo.Producer.DemoProducer))]

namespace SpeedRunTool.Demo.Producer;

/// <summary>
/// Publishes a synthetic payload on <see cref="DemoChannel.Id"/>.
/// </summary>
/// <remarks>
/// The smallest producer that is still realistic: it declares a channel, reports source
/// availability, and returns a payload per tick. A real producer replaces the counter with a memory
/// read and <see cref="IsSourceAvailable"/> with a live process check.
/// </remarks>
public sealed class DemoProducer : ProducerPluginBase<DemoPayload>
{
    private readonly DemoPayload payload = new();
    private long tick;

    /// <inheritdoc />
    public override IPluginInfo Info { get; } =
        PluginInfo.FromAssembly(typeof(DemoProducer).Assembly);

    /// <inheritdoc />
    public override PayloadChannelDescriptor Channel { get; } = new()
    {
        ChannelId = DemoChannel.Id,
        ContractVersion = DemoChannel.ContractVersion,
    };

    /// <summary>Always available: there is no external process to attach to.</summary>
    public override bool IsSourceAvailable => true;

    /// <inheritdoc />
    protected override JsonTypeInfo<DemoPayload> PayloadTypeInfo => DemoPayloadJsonContext.Default.DemoPayload;

    /// <inheritdoc />
    protected override ValueTask<DemoPayload?> RefreshAsync(CancellationToken cancellationToken)
    {
        // Mutating and returning the same instance is deliberate: the base class serialises before
        // returning, so no reference to it escapes, and a producer ticking 30 times a second should
        // not allocate a payload per tick.
        tick++;
        payload.Tick = tick;
        payload.Value = (int)(tick * 7 % 1000);
        payload.Ratio = (tick % 100) / 100f;
        payload.Label = $"tick-{tick}";
        return ValueTask.FromResult<DemoPayload?>(payload);
    }
}
