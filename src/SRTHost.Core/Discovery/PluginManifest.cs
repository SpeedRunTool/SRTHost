using System.Text.Json.Serialization;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Discovery;

/// <summary>
/// The host's reading of <c>srtplugin.json</c>.
/// </summary>
/// <remarks>
/// Deliberately a second declaration of the shape the build task writes, not a shared type. The
/// generator lives in <c>SRTPluginBase.BuildTasks</c>, which has to load inside both the .NET
/// Framework and the .NET MSBuild hosts and therefore serialises by hand rather than taking a
/// dependency on System.Text.Json; sharing a type would drag that constraint into the host. The two
/// are held together by <c>GeneratedManifestTests</c>, which asserts field by field that what the
/// build writes and what the plugin reports at run time agree.
/// <para>
/// This is what lets the router enumerate and describe plugins <em>without loading them</em>. That
/// is not an optimisation: the router is one AnyCPU process, and loading an x86 plugin into it to
/// read its metadata is the exact thing it must never do.
/// </para>
/// </remarks>
public sealed record PluginManifest
{
    /// <summary>The only schema version this host understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The manifest file name, beside the plugin's assembly.</summary>
    public const string FileName = "srtplugin.json";

    /// <summary>Manifest format version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Stable identity, shaped <c>&lt;Author&gt;.&lt;Subject&gt;.&lt;Name&gt;</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>One-line description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Who wrote it.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Assembly version, as four components.</summary>
    public string Version { get; init; } = "0.0.0.0";

    /// <summary>Contract generation the plugin was compiled against.</summary>
    public int ContractGeneration { get; init; }

    /// <summary>Producer or consumer, derived at build time from the interface the entry type implements.</summary>
    public PluginKind Kind { get; init; }

    /// <summary>Which runner can host this plugin.</summary>
    public PluginArchitecture Architecture { get; init; }

    /// <summary>Whether the runner must start a UI thread and message pump.</summary>
    public bool RequiresUiThread { get; init; }

    /// <summary>
    /// Reserved, always false in generation 5. A future <c>SRTHost.PluginRunnerDesktop64.exe</c> on
    /// <c>net10.0-windows</c> would be selected on this; the field exists now so adding it needs no
    /// schema bump.
    /// </summary>
    public bool RequiresWindowsDesktop { get; init; }

    /// <summary>File name of the assembly holding the entry type.</summary>
    public string EntryAssembly { get; init; } = string.Empty;

    /// <summary>Full name of the type implementing <c>IPlugin</c>.</summary>
    public string EntryType { get; init; } = string.Empty;
}

/// <summary>
/// Source-generated serialisation for <see cref="PluginManifest"/>.
/// </summary>
/// <remarks>
/// <see cref="JsonStringEnumConverter"/> because the generator writes <c>"Producer"</c> and
/// <c>"X64"</c>, which is the right call for a file a plugin author may end up reading in a bug
/// report - and which means the enum's numeric values stay free to change without invalidating
/// every manifest on disk.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(PluginManifest))]
public sealed partial class PluginManifestJsonContext : JsonSerializerContext;
