using System.Text.Json;
using System.Text.Json.Serialization;

namespace SRTHost.Core;

/// <summary>
/// Serialiser options for the two documents the host reads off disk.
/// </summary>
/// <remarks>
/// These are the only reflection-based serialisation in the solution, and they are deliberate.
/// <para>
/// <b>The System.Text.Json source generator does not run property initialisers when deserialising.</b>
/// Measured on .NET 10.0.11, not assumed: a record with <c>public string Name { get; init; } =
/// string.Empty;</c> deserialised through a generated <see cref="JsonSerializerContext"/> comes back
/// with <c>Name</c> null when the JSON omits the field, and a <c>bool</c> defaulted to true comes
/// back false. Reflection-based deserialisation of the same type keeps both. Neither an explicit
/// <c>[JsonConstructor]</c> parameterless constructor nor
/// <c>[JsonObjectCreationHandling(Populate)]</c> fixes it for <c>init</c> properties - Populate works
/// only when every property has a <c>set</c> accessor, which would mean giving up immutability on
/// two records that are passed around freely. A positional record whose primary constructor carries
/// default parameter values does keep them, because those defaults are constructor metadata rather
/// than field initialisers, but that shape fits neither of these types.
/// </para>
/// <para>
/// That behaviour is invisible until a document omits a field, and both documents here are exactly
/// the kind that does: a <c>host.json</c> written by an older version omits every field a newer
/// version added, and an <c>srtplugin.json</c> may legitimately carry no description or author. The
/// failure is silent and wrong - settings quietly reset, a plugin's name arriving as null through a
/// property the compiler believes is non-nullable - so it is not worth the startup-time saving here.
/// </para>
/// <para>
/// Everything on the wire keeps its generated context and should: <see cref="SRTHost.Ipc"/>'s
/// messages are written and read by code in this repository on a hot path, the runner is a
/// short-lived process where reflection metadata would sit on the handshake's critical path, and no
/// message type relies on an initialiser default to be correct. If one ever does, it belongs here
/// instead - see <c>PluginManifestDefaultsTests</c>, which is what pins this down.
/// </para>
/// </remarks>
public static class SrtJson
{
    /// <summary>Options for <c>%LOCALAPPDATA%\SRTHost\host.json</c>.</summary>
    /// <remarks>Indented because a person edits this file by hand when the UI cannot start.</remarks>
    public static JsonSerializerOptions HostSettings { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Options for a plugin's <c>srtplugin.json</c>.</summary>
    /// <remarks>
    /// Trailing commas and comments are tolerated because this file is hand-written often enough -
    /// by anyone assembling a plugin folder without the build task - that refusing it over a comma
    /// would be a bad trade. Enums are read by name, matching what the generator writes.
    /// </remarks>
    public static JsonSerializerOptions PluginManifest { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };
}
