using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using SRTPluginBase.Abstractions;
using SpeedRunTool.Demo.Contracts;

[assembly: SrtPluginAssembly("SpeedRunTool.Demo.Consumer", typeof(SpeedRunTool.Demo.Consumer.DemoConsumer))]

namespace SpeedRunTool.Demo.Consumer;

/// <summary>
/// Logs whatever arrives on <see cref="DemoChannel.Id"/>, as its settings say to.
/// </summary>
/// <remarks>
/// A consumer that is also configurable, which is the ordinary shape rather than an exotic one - an
/// overlay has a colour, a corner and a set of values to show. That combination is why
/// <see cref="ConfigurableConsumerPluginBase{TPayload, TConfiguration}"/> exists: C# gives a type one
/// base class, so consuming and being configurable could not each come from a separate one.
/// </remarks>
public sealed class DemoConsumer : ConfigurableConsumerPluginBase<DemoPayload, DemoConsumerConfiguration>
{
    private long seen;

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
    protected override JsonTypeInfo<DemoConsumerConfiguration> ConfigurationTypeInfo
        => DemoConsumerConfigurationJsonContext.Default.DemoConsumerConfiguration;

    /// <inheritdoc />
    /// <remarks>
    /// Reacting here rather than reading <c>Configuration</c> on every payload is the pattern to
    /// copy: settings change a handful of times in a session and payloads arrive thirty times a
    /// second.
    /// </remarks>
    public override ValueTask OnConfigurationChangedAsync(
        DemoConsumerConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Logger.LogInformation(
            "Settings applied: enabled={Enabled} corner={Corner} fields={Fields} every={LogEvery}.",
            configuration.Enabled,
            configuration.Corner,
            configuration.Fields,
            configuration.LogEvery);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnPayloadAsync(DemoPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        DemoConsumerConfiguration settings = Configuration;

        seen++;

        if (!settings.Enabled || seen % Math.Max(settings.LogEvery, 1) != 0)
            return ValueTask.CompletedTask;

        if (settings.IgnoredLabels.Contains(payload.Label, StringComparer.OrdinalIgnoreCase))
            return ValueTask.CompletedTask;

        // Assembled from the flags rather than logged wholesale, so the settings visibly do
        // something: unticking a field really does take it out of the log, which is what makes the
        // generated form demonstrable rather than merely present.
        List<string> parts = [];

        if (settings.Fields.HasFlag(DemoFields.Tick))
            parts.Add($"tick={payload.Tick}");

        if (settings.Fields.HasFlag(DemoFields.Value))
            parts.Add($"value={payload.Value}");

        if (settings.Fields.HasFlag(DemoFields.Ratio))
            parts.Add($"ratio={payload.Ratio:F2}");

        if (settings.Fields.HasFlag(DemoFields.Label))
            parts.Add($"label={payload.Label}");

        Logger.LogInformation(
            "[{Prefix}] {Corner}: {Payload}",
            settings.Prefix,
            settings.Corner,
            string.Join(' ', parts));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask OnChannelClosedAsync(string channelId, CancellationToken cancellationToken)
    {
        // A real overlay clears its display here, after Configuration.Linger. Showing the last frame
        // of a game that has exited is worse than showing nothing.
        Logger.LogInformation("Channel {ChannelId} closed.", channelId);
        return ValueTask.CompletedTask;
    }
}
