using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SRTHost.Ipc;

/// <summary>
/// Source-generated serialisation for every control message and the log record.
/// </summary>
/// <remarks>
/// Source generation rather than reflection, for two reasons that both matter here. The runner is a
/// small short-lived process launched once per plugin, and reflection-based serialisation would have
/// it building metadata for the whole message hierarchy during startup, on the critical path to the
/// handshake. And it keeps the door open to trimming the runner later without the serialiser being
/// the thing that stops it.
/// <para>
/// Payload traffic deliberately does not appear here. The router never deserialises a payload - that
/// is what lets it stay one AnyCPU process while the plugins it supervises are x86 or x64 - so the
/// only types it needs to understand are its own.
/// </para>
/// </remarks>
/// <remarks>
/// Serialise through <c>IpcJsonContext.Default.IpcMessage</c> rather than through the options bag:
/// the generated <see cref="JsonTypeInfo{T}"/> is the whole point of the source generator, and going
/// via options re-enters the reflection path this exists to avoid.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(IpcLogRecord))]
public sealed partial class IpcJsonContext : JsonSerializerContext;
