using System.Text.Json;
using System.Text.Json.Serialization;

namespace SRTOverlay.Protocol;

/// <summary>
/// Everything the shim is told at startup, written into the target process by the injector and
/// passed to <see cref="OverlayProtocol.StartExport"/> as a UTF-16 JSON string.
/// </summary>
/// <remarks>
/// <para>
/// A blob rather than a handful of separate remote allocations because there is exactly one moment
/// to hand anything over - the second <c>CreateRemoteThread</c> - and it takes one pointer.
/// </para>
/// <para>
/// JSON rather than a packed struct because both ends are managed and the layout would otherwise
/// have to agree across two architectures by hand. It is parsed once, off the render path, with a
/// source-generated context so nothing reflects.
/// </para>
/// </remarks>
public sealed record OverlayStartupOptions
{
    /// <summary>Must equal <see cref="OverlayProtocol.Version"/> or the shim refuses to start.</summary>
    /// <remarks>
    /// No initialiser, and that is deliberate: the source-generated serialiser does not run property
    /// initialisers, so a default here would be a lie on the reading side. An omitted version reads
    /// as zero and is refused, which is exactly what should happen to a blob that does not state one.
    /// See <c>SrtJson</c> in <c>SRTHost.Core</c> for the measurement behind that claim.
    /// </remarks>
    public int ProtocolVersion { get; init; }

    /// <summary>
    /// Base name of the process the injector believes it is in, with extension - <c>re9.exe</c>.
    /// </summary>
    /// <remarks>
    /// Checked case-insensitively against the running module before anything else happens. This is
    /// the "refuses to run in a process the plugin's manifest did not name" requirement from the
    /// plan's section 11, and it is why it costs a string compare rather than a design.
    /// </remarks>
    public required string TargetProcess { get; init; }

    /// <summary>
    /// Name of the named pipe back to the overlay plugin's runner, without the <c>\.\pipe\</c>
    /// prefix. Absent or empty means "do not connect", which is what the injection spike passes.
    /// </summary>
    /// <remarks>
    /// Nullable, along with the two below, because the source-generated serialiser drops property
    /// initialisers: a <c>= string.Empty</c> default would silently become null for any blob that
    /// omits the field, through a property the compiler had promised was non-null. Stating the
    /// nullability is honest and costs one <see cref="string.IsNullOrEmpty(string)"/> at each use.
    /// </remarks>
    public string? PipeName { get; init; }

    /// <summary>
    /// Full path of the log file the shim writes.
    /// </summary>
    /// <remarks>
    /// Passed in rather than derived because the shim's working directory is the game's, and the
    /// reference tool dropping <c>SRTPluginRE9.log</c> into the game's install folder is a thing to
    /// stop doing rather than to copy. Empty disables file logging entirely.
    /// </remarks>
    public string? LogPath { get; init; }

    /// <summary>Identifies this host session in the log, so two hosts do not read as one.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Process id of the overlay plugin's runner - the thing that injected this shim. Zero means
    /// nobody owns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shim waits on this process and detaches itself when it exits. That is not a convenience:
    /// once the shim hooks <c>Present</c>, a runner that crashes without saying so would otherwise
    /// leave a detour installed in someone's game with nothing left alive that could remove it. An
    /// orphaned shim is strictly worse than no shim.
    /// </para>
    /// <para>
    /// It also covers the case an orderly shutdown cannot: the runner being killed, which is exactly
    /// what the host's own supervisor does to a plugin that stops responding.
    /// </para>
    /// </remarks>
    public int OwnerProcessId { get; init; }

    /// <summary>
    /// Overrides the composite brightness the shim would pick for the back buffer's format. Null means
    /// use the per-format default. For an HDR back buffer this scales the SDR overlay towards the
    /// display's reference white, and it exists so that look can be tuned live rather than rebuilt.
    /// </summary>
    public float? OverlayBrightness { get; init; }

    /// <summary>
    /// Forces PQ (ST 2084) encoding of the composited overlay, for an HDR10 10-bit back buffer that a
    /// format check cannot distinguish from a 10-bit SDR one. Null/false leaves the per-format default.
    /// </summary>
    public bool? OverlayForcePq { get; init; }

    /// <summary>Serialise for the wire. Never reflects; see <see cref="OverlayJsonContext"/>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, OverlayJsonContext.Default.OverlayStartupOptions);

    /// <summary>
    /// Parse a blob read out of remote memory. Returns <see langword="null"/> for anything malformed
    /// rather than throwing, because the only caller is a boundary that may not let an exception out.
    /// </summary>
    public static OverlayStartupOptions? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, OverlayJsonContext.Default.OverlayStartupOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Source-generated serialisation for everything that crosses the shim boundary.
/// </summary>
/// <remarks>
/// Source generation is not an optimisation here, it is a requirement: the reflection-based
/// serialiser does not survive trimming, and this assembly is linked into a NativeAOT binary.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OverlayStartupOptions))]
public sealed partial class OverlayJsonContext : JsonSerializerContext;
