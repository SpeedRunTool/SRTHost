using System.Text.Json.Serialization;
using SRTPluginBase.Abstractions;

namespace SRTHost.Ipc;

/// <summary>
/// Base of every control message.
/// </summary>
/// <remarks>
/// Control traffic is JSON, deliberately, against a wire format that is otherwise binary. There are
/// a handful of these per plugin lifecycle - not per tick - so their cost is irrelevant, and being
/// able to dump one verbatim into a log and read it is worth far more than the bytes. The hot path
/// pays none of it: payloads travel on <see cref="IpcChannel.Data"/> as bytes the router never
/// decodes.
/// <para>
/// The discriminator is written as <c>$kind</c> and its value is the <see cref="ControlMessageKind"/>
/// member name, which is also what the frame header carries numerically. <c>MessageKindTests</c>
/// holds those two representations to each other so a message cannot be added to one and forgotten
/// in the other.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(HelloMessage), nameof(ControlMessageKind.Hello))]
[JsonDerivedType(typeof(LoadPluginMessage), nameof(ControlMessageKind.LoadPlugin))]
[JsonDerivedType(typeof(StartPluginMessage), nameof(ControlMessageKind.StartPlugin))]
[JsonDerivedType(typeof(StopPluginMessage), nameof(ControlMessageKind.StopPlugin))]
[JsonDerivedType(typeof(UnloadPluginMessage), nameof(ControlMessageKind.UnloadPlugin))]
[JsonDerivedType(typeof(ApplyConfigurationMessage), nameof(ControlMessageKind.ApplyConfiguration))]
[JsonDerivedType(typeof(GetConfigurationSchemaMessage), nameof(ControlMessageKind.GetConfigurationSchema))]
[JsonDerivedType(typeof(SetProduceIntervalMessage), nameof(ControlMessageKind.SetProduceInterval))]
[JsonDerivedType(typeof(PingMessage), nameof(ControlMessageKind.Ping))]
[JsonDerivedType(typeof(ShutdownMessage), nameof(ControlMessageKind.Shutdown))]
[JsonDerivedType(typeof(HelloAckMessage), nameof(ControlMessageKind.HelloAck))]
[JsonDerivedType(typeof(ReadyMessage), nameof(ControlMessageKind.Ready))]
[JsonDerivedType(typeof(PluginStatusChangedMessage), nameof(ControlMessageKind.PluginStatusChanged))]
[JsonDerivedType(typeof(SourceAvailabilityChangedMessage), nameof(ControlMessageKind.SourceAvailabilityChanged))]
[JsonDerivedType(typeof(FaultMessage), nameof(ControlMessageKind.Fault))]
[JsonDerivedType(typeof(HeartbeatMessage), nameof(ControlMessageKind.Heartbeat))]
[JsonDerivedType(typeof(AckMessage), nameof(ControlMessageKind.Ack))]
[JsonDerivedType(typeof(ErrorMessage), nameof(ControlMessageKind.Error))]
[JsonDerivedType(typeof(ConfigurationSchemaMessage), nameof(ControlMessageKind.ConfigurationSchema))]
public abstract record IpcMessage
{
    /// <summary>
    /// The kind stamped into the frame header, so a frame can be routed and counted without its body
    /// being parsed.
    /// </summary>
    [JsonIgnore]
    public abstract ControlMessageKind Kind { get; }
}

#region Router to runner

/// <summary>Opening handshake. The router speaks first.</summary>
public sealed record HelloMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.Hello;

    /// <summary>Protocol version the router speaks. See <see cref="IpcProtocol.Version"/>.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Contract generation the router can load. See <c>SrtContract.Generation</c>.</summary>
    public required int ContractGeneration { get; init; }

    /// <summary>The router's per-launch session id, echoed for diagnostics.</summary>
    public required string SessionId { get; init; }
}

/// <summary>Load the plugin assembly and instantiate its entry type.</summary>
public sealed record LoadPluginMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.LoadPlugin;

    /// <summary>The plugin's stable id, used for its configuration and log category.</summary>
    public required string PluginId { get; init; }

    /// <summary>Directory holding the plugin and its private dependencies.</summary>
    public required string PluginDirectory { get; init; }

    /// <summary>File name of the assembly carrying the entry type.</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>Full name of the type implementing <c>IPlugin</c>.</summary>
    public required string EntryType { get; init; }

    /// <summary>Stored configuration to apply before the plugin starts, if there is any.</summary>
    public string? ConfigurationJson { get; init; }
}

/// <summary>Start the loaded plugin.</summary>
public sealed record StartPluginMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.StartPlugin;
}

/// <summary>Stop the plugin but keep it loaded.</summary>
public sealed record StopPluginMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.StopPlugin;
}

/// <summary>Dispose the plugin. Reload is a process recycle, so the runner exits afterwards.</summary>
public sealed record UnloadPluginMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.UnloadPlugin;
}

/// <summary>Push a new configuration to the plugin.</summary>
public sealed record ApplyConfigurationMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.ApplyConfiguration;

    /// <summary>The configuration document, as stored in <c>config\{pluginId}.json</c>.</summary>
    public required string ConfigurationJson { get; init; }
}

/// <summary>Ask for the plugin's configuration schema, to render the settings form.</summary>
public sealed record GetConfigurationSchemaMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.GetConfigurationSchema;
}

/// <summary>Change a producer's tick interval.</summary>
public sealed record SetProduceIntervalMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.SetProduceInterval;

    /// <summary>Interval in milliseconds. The runner clamps this; see <c>ProduceInterval</c>.</summary>
    public required int IntervalMilliseconds { get; init; }
}

/// <summary>Liveness probe, answered with <see cref="AckMessage"/>.</summary>
public sealed record PingMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.Ping;
}

/// <summary>Shut down cleanly.</summary>
public sealed record ShutdownMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.Shutdown;

    /// <summary>Why, for the runner's log.</summary>
    public string? Reason { get; init; }
}

#endregion

#region Runner to router

/// <summary>Answer to <see cref="HelloMessage"/>.</summary>
public sealed record HelloAckMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.HelloAck;

    /// <summary>The runner's process id, so the supervisor can kill it if it stops answering.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Which runner this is. Decides whether a given plugin can be hosted here at all.</summary>
    public required PluginArchitecture Architecture { get; init; }

    /// <summary>The .NET runtime the runner is on, for the diagnostics view.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Protocol version the runner speaks.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Contract generation the runner was compiled against.</summary>
    public required int ContractGeneration { get; init; }
}

/// <summary>Describes a channel a producer publishes.</summary>
public sealed record ChannelDescriptor
{
    /// <summary>Channel id consumers bind to, e.g. <c>srt/re4r/gamememory</c>.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Payload contract version, as <c>major.minor</c>.</summary>
    public required string ContractVersion { get; init; }

    /// <summary>How payloads on this channel are encoded.</summary>
    public PayloadCodec Codec { get; init; }

    /// <summary>JSON Schema for the payload, when the plugin supplies one. Diagnostics only.</summary>
    public string? SchemaJson { get; init; }
}

/// <summary>Describes a consumer's interest in a channel.</summary>
public sealed record SubscriptionDescriptor
{
    /// <summary>Channel to subscribe to. <c>*</c> means every channel.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Lowest payload contract version this consumer accepts.</summary>
    public string? MinimumContractVersion { get; init; }

    /// <summary>Whether the consumer is useless without this channel.</summary>
    public bool Required { get; init; }
}

/// <summary>The plugin is loaded and has declared what it publishes or consumes.</summary>
public sealed record ReadyMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.Ready;

    /// <summary>What the plugin is. Decides which of the two properties below is populated.</summary>
    public required PluginKind PluginKind { get; init; }

    /// <summary>The channel this producer publishes, or null for a consumer.</summary>
    public ChannelDescriptor? Channel { get; init; }

    /// <summary>What this consumer subscribes to, empty for a producer.</summary>
    public IReadOnlyList<SubscriptionDescriptor> Subscriptions { get; init; } = [];

    /// <summary>Whether the plugin needs a UI thread, echoed from its manifest.</summary>
    public bool RequiresUiThread { get; init; }
}

/// <summary>The plugin's lifecycle state changed.</summary>
public sealed record PluginStatusChangedMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.PluginStatusChanged;

    /// <summary>The new state.</summary>
    public required PluginStatus Status { get; init; }

    /// <summary>Why, when the state is a failure.</summary>
    public PluginSubStatus SubStatus { get; init; }

    /// <summary>Human-readable detail for the UI and the log.</summary>
    public string? Detail { get; init; }
}

/// <summary>A producer's source appeared or went away - usually the game process.</summary>
public sealed record SourceAvailabilityChangedMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.SourceAvailabilityChanged;

    /// <summary>Whether the source is available now.</summary>
    public required bool Available { get; init; }
}

/// <summary>Something went wrong.</summary>
public sealed record FaultMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.Fault;

    /// <summary>What happened.</summary>
    public required string Message { get; init; }

    /// <summary>Stack trace, when there is one.</summary>
    public string? StackTrace { get; init; }

    /// <summary>
    /// True when the runner is about to die. Sent best-effort before <c>FailFast</c>, so it may not
    /// arrive at all - the supervisor must not depend on seeing it.
    /// </summary>
    public bool Fatal { get; init; }
}

/// <summary>Unprompted liveness signal.</summary>
public sealed record HeartbeatMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.Heartbeat;

    /// <summary>How long the runner has been up, for the diagnostics view.</summary>
    public required long UptimeMilliseconds { get; init; }
}

#endregion

#region Responses

/// <summary>Generic success, correlated to a request.</summary>
public sealed record AckMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.Ack;
}

/// <summary>Generic failure, correlated to a request.</summary>
public sealed record ErrorMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.Error;

    /// <summary>What went wrong.</summary>
    public required string Message { get; init; }

    /// <summary>Classification, when the runner could determine one.</summary>
    public PluginSubStatus SubStatus { get; init; }

    /// <summary>Stack trace or other detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>Answer to <see cref="GetConfigurationSchemaMessage"/>.</summary>
public sealed record ConfigurationSchemaMessage : IpcMessage
{
    /// <inheritdoc />
    public override ControlMessageKind Kind => ControlMessageKind.ConfigurationSchema;

    /// <summary>The schema the settings form is generated from.</summary>
    public required string SchemaJson { get; init; }

    /// <summary>The plugin's current configuration values.</summary>
    public required string CurrentJson { get; init; }
}

#endregion
