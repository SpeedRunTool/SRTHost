using System.Text.Json;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Discovery;

/// <summary>One plugin found on disk, ready to be handed to a runner.</summary>
public sealed record DiscoveredPlugin
{
    /// <summary>What the manifest says about it.</summary>
    public required PluginManifest Manifest { get; init; }

    /// <summary>The directory it lives in.</summary>
    public required string Directory { get; init; }

    /// <summary>Full path of the assembly holding the entry type.</summary>
    public required string EntryAssemblyPath { get; init; }

    /// <summary>Shorthand for the plugin's id.</summary>
    public string Id => Manifest.Id;

    /// <summary>Which runner architecture this plugin needs.</summary>
    public PluginArchitecture Architecture => Manifest.Architecture;
}

/// <summary>Something in the plugins directory that could not be used, and why.</summary>
/// <remarks>
/// Returned rather than thrown, and returned rather than logged and forgotten. One unusable plugin
/// folder must never stop the other nineteen from loading - but it must also be visible, because
/// "my plugin does not appear" with nothing in the log is the single least diagnosable failure this
/// application has.
/// </remarks>
public sealed record DiscoveryProblem(string Path, string Reason);

/// <summary>The result of one sweep of the plugins directory.</summary>
public sealed record DiscoveryResult
{
    /// <summary>Plugins this host can run.</summary>
    public required IReadOnlyList<DiscoveredPlugin> Plugins { get; init; }

    /// <summary>Everything else, with a reason apiece.</summary>
    public required IReadOnlyList<DiscoveryProblem> Problems { get; init; }
}

/// <summary>
/// Finds installed plugins by reading their manifests.
/// </summary>
public static class PluginDiscovery
{
    /// <summary>
    /// Sweeps <paramref name="pluginsDirectory"/> for usable plugins.
    /// </summary>
    /// <remarks>
    /// Layout is one directory per plugin, each holding a <c>srtplugin.json</c>. The directory name
    /// is <em>not</em> significant - the manifest names the entry assembly - which relaxes
    /// generation 1's rule that the DLL had to be named after its folder. Identity comes from the
    /// manifest, and only from there.
    /// <para>
    /// A missing manifest is reported rather than probed. The design reserves a
    /// spawn-a-runner-in-probe-mode fallback for plugins built before the generator existed; nothing
    /// in generation 5 can be built without one, so that fallback is not implemented and its absence
    /// is a legible error instead of a silent omission.
    /// </para>
    /// </remarks>
    public static DiscoveryResult Scan(string pluginsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        List<DiscoveredPlugin> plugins = [];
        List<DiscoveryProblem> problems = [];

        if (!Directory.Exists(pluginsDirectory))
            return new DiscoveryResult { Plugins = plugins, Problems = problems };

        Dictionary<string, string> claimedIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in Directory.EnumerateDirectories(pluginsDirectory))
        {
            string manifestPath = Path.Combine(directory, PluginManifest.FileName);

            if (!File.Exists(manifestPath))
            {
                problems.Add(new DiscoveryProblem(directory, $"No {PluginManifest.FileName}."));
                continue;
            }

            PluginManifest? manifest;

            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(manifestPath),
                    SrtJson.PluginManifest);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new DiscoveryProblem(manifestPath, $"Unreadable: {ex.Message}"));
                continue;
            }

            if (manifest is null)
            {
                problems.Add(new DiscoveryProblem(manifestPath, "Deserialised to null."));
                continue;
            }

            if (manifest.SchemaVersion != PluginManifest.CurrentSchemaVersion)
            {
                problems.Add(new DiscoveryProblem(
                    manifestPath,
                    $"Schema version {manifest.SchemaVersion}; this host reads {PluginManifest.CurrentSchemaVersion}."));
                continue;
            }

            // The id becomes a settings file name, a state directory name and part of a pipe name.
            // It is validated at build time (SRT1007), but a manifest can arrive beside a downloaded
            // plugin having never passed through the generator, so it is re-validated here - the one
            // place that reads it off disk.
            if (!IsSafeId(manifest.Id))
            {
                problems.Add(new DiscoveryProblem(
                    manifestPath,
                    $"Id '{manifest.Id}' must be letters, digits, '.', '_' or '-' with no empty segments."));
                continue;
            }

            if (claimedIds.TryGetValue(manifest.Id, out string? firstClaim))
            {
                // Two folders claiming one id would share a settings file, a state directory and a
                // pipe name. Both are refused rather than picking one, because which of the two the
                // user meant is not knowable from here.
                problems.Add(new DiscoveryProblem(
                    manifestPath,
                    $"Id '{manifest.Id}' is already claimed by '{firstClaim}'."));
                continue;
            }

            if (manifest.ContractGeneration != SrtContract.Generation)
            {
                problems.Add(new DiscoveryProblem(
                    manifestPath,
                    $"Built against contract generation {manifest.ContractGeneration}; "
                    + $"this host loads generation {SrtContract.Generation}."));
                continue;
            }

            if (manifest.RequiresWindowsDesktop)
            {
                // Reserved in the schema and honoured nowhere: no runner declares
                // Microsoft.WindowsDesktop.App, so a WinForms or WPF plugin cannot be hosted at all.
                // Saying so is much better than loading it and failing on assembly resolution.
                problems.Add(new DiscoveryProblem(
                    manifestPath,
                    "Requires the Windows Desktop framework, which no runner in this generation provides."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
            {
                problems.Add(new DiscoveryProblem(manifestPath, "No entry assembly or entry type."));
                continue;
            }

            // Rejected rather than combined: a manifest is not a trusted document, and an
            // entryAssembly of "..\..\something.dll" would otherwise load an assembly from outside
            // the plugin's own directory.
            if (manifest.EntryAssembly.AsSpan().IndexOfAny('/', '\\', ':') >= 0)
            {
                problems.Add(new DiscoveryProblem(
                    manifestPath,
                    $"Entry assembly '{manifest.EntryAssembly}' must be a file name, not a path."));
                continue;
            }

            string entryAssemblyPath = Path.Combine(directory, manifest.EntryAssembly);

            if (!File.Exists(entryAssemblyPath))
            {
                problems.Add(new DiscoveryProblem(manifestPath, $"'{manifest.EntryAssembly}' is not in this folder."));
                continue;
            }

            claimedIds[manifest.Id] = manifestPath;

            plugins.Add(new DiscoveredPlugin
            {
                Manifest = manifest,
                Directory = Path.GetFullPath(directory),
                EntryAssemblyPath = Path.GetFullPath(entryAssemblyPath),
            });
        }

        return new DiscoveryResult
        {
            Plugins = plugins,
            Problems = problems,
        };
    }

    /// <summary>
    /// Whether <paramref name="id"/> is safe as a file name, a directory name and a pipe name
    /// component.
    /// </summary>
    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        foreach (Range segment in id.AsSpan().Split('.'))
        {
            if (id.AsSpan()[segment].IsEmpty)
                return false;
        }

        foreach (char character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }
}
