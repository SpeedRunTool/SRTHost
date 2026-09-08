using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SRTHost.Core.Supervision;

namespace SRTHost.Core;

/// <summary>
/// Everything the user can change about the host itself, as stored in
/// <c>%LOCALAPPDATA%\SRTHost\host.json</c>.
/// </summary>
/// <remarks>
/// Deliberately a plain serialisable record with defaults on every property, so a missing file, a
/// truncated one, or one written by a future version that added fields all produce a working host
/// rather than a startup failure. The host's own settings are the one thing that must never need
/// the UI to repair, because the UI is what they configure.
/// <para>
/// Plugin settings are <em>not</em> here. They live per plugin in <c>config\&lt;pluginId&gt;.json</c>
/// and the host never parses them - see <see cref="HostPaths.ConfigFile"/>.
/// </para>
/// </remarks>
public sealed record HostSettings
{
    /// <summary>The settings file.</summary>
    public static string FilePath => Path.Combine(HostPaths.DataDirectory, "host.json");

    /// <summary>
    /// How the settings file is read and written.
    /// </summary>
    /// <remarks>
    /// Reflection-based, and it is the one place in this solution that is - everything else goes
    /// through a source-generated <see cref="JsonSerializerContext"/>. The generator's deserialiser
    /// does not run property initialisers, which was measured rather than assumed: a file omitting
    /// <c>closeToTray</c> came back with it <see langword="false"/> instead of its declared default,
    /// and the same would have happened to the log level, the retention count and the restart policy.
    /// That is precisely the failure this type is written to avoid, since a settings file written by
    /// an older version omits exactly the fields a newer version added.
    /// <para>
    /// Nothing is lost by it here: the host is not trimmed, single-file or AOT-compiled (see
    /// <c>Directory.Build.props</c>), this runs twice per launch, and the type is a dozen scalars.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Where to look for plugins, or null for the folder beside the executable.</summary>
    /// <remarks>
    /// Null rather than the resolved default, so a user who never changed it keeps following the
    /// install rather than being pinned to wherever the host happened to live when they first ran it.
    /// That matters here: the installer puts the host in <c>%LOCALAPPDATA%</c> and an update can move
    /// it.
    /// </remarks>
    public string? PluginsDirectory { get; init; }

    /// <summary>Minimum level for the host's own output.</summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary>Minimum level for output forwarded from runners.</summary>
    /// <remarks>
    /// Separate from <see cref="LogLevel"/> on purpose, and the reasoning is the same as for the
    /// command-line flags: a producer at 30 Hz and a consumer logging each payload together write
    /// sixty lines a second, which buries everything the host says about wiring, restarts and
    /// latency.
    /// </remarks>
    public LogLevel RunnerLogLevel { get; init; } = LogLevel.Information;

    /// <summary>How many log files to keep.</summary>
    public int LogRetention { get; init; } = 10;

    /// <summary>Whether closing the window hides to the tray instead of exiting.</summary>
    /// <remarks>
    /// On by default, because it is what a streamer wants and because the alternative is worse than
    /// it looks: the runners are children of this process and die with it, so closing the window
    /// while a game is running would take the overlay down mid-run.
    /// </remarks>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Whether to start with no window shown.</summary>
    public bool StartMinimized { get; init; }

    /// <summary>Whether the host launches at sign-in.</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>What to do when a runner exits unexpectedly.</summary>
    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.OnCrash;

    /// <summary>Restarts allowed inside the restart window before a plugin is left faulted.</summary>
    public int MaximumRestarts { get; init; } = 5;

    /// <summary>Plugin ids the user has switched off. Discovery still reports them; nothing starts them.</summary>
    public IReadOnlyList<string> DisabledPlugins { get; init; } = [];

    /// <summary>Reads the settings file, or returns defaults when there is nothing usable to read.</summary>
    /// <remarks>
    /// Every failure path here returns defaults rather than throwing. A settings file is not worth a
    /// dialog on startup, and the user's next Save rewrites whatever was wrong with it.
    /// </remarks>
    public static HostSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(FilePath), SerializerOptions)
                    ?? new HostSettings()
                : new HostSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HostSettings();
        }
    }

    /// <summary>Writes the settings file.</summary>
    /// <returns>Null on success, or why it could not be written.</returns>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(HostPaths.DataDirectory);

            // Written to a temporary file and moved into place. A host killed mid-write - which is
            // exactly what Ctrl+Alt+Del during a game does - would otherwise leave a half-written
            // file that Load() then discards, silently resetting every setting the user had.
            string temporary = FilePath + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(this, SerializerOptions));

            File.Move(temporary, FilePath, overwrite: true);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Serialises settings exactly as <see cref="Save"/> writes them.</summary>
    /// <remarks>
    /// Exposed so the file's shape can be asserted without writing to
    /// <c>%LOCALAPPDATA%</c> - a test that used <see cref="Save"/> would clobber the developer's own
    /// settings, and one that reimplemented the options would stop testing what ships.
    /// </remarks>
    public static string ToJson(HostSettings settings) => JsonSerializer.Serialize(settings, SerializerOptions);

    /// <summary>Deserialises settings exactly as <see cref="Load"/> reads them.</summary>
    public static HostSettings? FromJson(string json) => JsonSerializer.Deserialize<HostSettings>(json, SerializerOptions);

    /// <summary>Turns these settings into the runtime options the host is built from.</summary>
    public HostRuntimeOptions ToRuntimeOptions() => new()
    {
        PluginsDirectory = string.IsNullOrWhiteSpace(PluginsDirectory)
            ? HostPaths.DefaultPluginsDirectory
            : PluginsDirectory,
        Supervisor = new SupervisorOptions
        {
            RunnerLogLevel = RunnerLogLevel,
            RestartPolicy = RestartPolicy,
            MaximumRestarts = MaximumRestarts,
        },
        DisabledPlugins = new HashSet<string>(DisabledPlugins, StringComparer.OrdinalIgnoreCase),
    };
}

