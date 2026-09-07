namespace SRTHost.Ipc;

/// <summary>
/// Identifies a control message in the frame header, so a frame can be routed, counted or logged
/// without parsing its JSON body.
/// </summary>
/// <remarks>
/// Every value is part of the wire format. **Never renumber one**, and never reuse a retired value:
/// a runner and the router are separate executables that an in-place update can leave at different
/// versions for the length of a restart.
/// <para>
/// Ranges are blocked out by direction purely so an unexpected value is obvious in a log. Nothing
/// enforces them, because <see cref="ControlMessageKind.Ping"/> and friends are legitimate in either
/// direction.
/// </para>
/// <para>
/// Data and log traffic deliberately has no kind - the header carries zero there. Payloads travel on
/// <see cref="IpcChannel.Data"/> as opaque bytes behind a <see cref="DataFrameHeader"/>, and which
/// of "a producer published this" or "deliver this to a consumer" it means is decided by the
/// direction it travelled, not by a field. That is what keeps the router from having to understand,
/// or even decode, a single payload.
/// </para>
/// </remarks>
public enum ControlMessageKind : ushort
{
    /// <summary>Not a control frame. The value carried on Data and Log frames.</summary>
    None = 0,

    #region Router to runner

    /// <summary>Opening handshake, carrying the protocol and contract generation the router speaks.</summary>
    Hello = 1,

    /// <summary>Load the plugin assembly and instantiate its entry type.</summary>
    LoadPlugin = 2,

    /// <summary>Start the loaded plugin.</summary>
    StartPlugin = 3,

    /// <summary>Stop the plugin but keep it loaded.</summary>
    StopPlugin = 4,

    /// <summary>Dispose the plugin. The runner then exits; reload is a process recycle.</summary>
    UnloadPlugin = 5,

    /// <summary>Push a new configuration to the plugin.</summary>
    ApplyConfiguration = 6,

    /// <summary>Ask for the plugin's configuration schema, for the generated settings form.</summary>
    GetConfigurationSchema = 7,

    /// <summary>Change a producer's tick interval.</summary>
    SetProduceInterval = 8,

    /// <summary>Liveness probe. Answered with <see cref="Ack"/>.</summary>
    Ping = 9,

    /// <summary>Shut down cleanly. The runner disposes the plugin and exits.</summary>
    Shutdown = 10,

    /// <summary>
    /// A channel a consumer subscribes to has gone away, so it can clear what it is displaying.
    /// </summary>
    ChannelClosed = 11,

    #endregion

    #region Runner to router

    /// <summary>Answer to <see cref="Hello"/>: pid, architecture, runtime and generations.</summary>
    HelloAck = 100,

    /// <summary>The plugin is loaded and has declared its channel or its subscriptions.</summary>
    Ready = 101,

    /// <summary>The plugin's lifecycle state changed.</summary>
    PluginStatusChanged = 102,

    /// <summary>A producer's source appeared or went away - usually the game process.</summary>
    SourceAvailabilityChanged = 103,

    /// <summary>Something went wrong. <c>Fatal</c> means the runner is about to die.</summary>
    Fault = 104,

    /// <summary>Periodic liveness signal, unprompted.</summary>
    Heartbeat = 105,

    #endregion

    #region Responses

    /// <summary>Generic success, correlated to a request.</summary>
    Ack = 200,

    /// <summary>Generic failure, correlated to a request.</summary>
    Error = 201,

    /// <summary>Answer to <see cref="GetConfigurationSchema"/>.</summary>
    ConfigurationSchema = 202,

    #endregion
}
