using System.Text;

namespace SRTHost.Core.Configuration;

/// <summary>
/// The host's copy of every plugin's settings file.
/// </summary>
/// <remarks>
/// The host reads these to fill <c>LoadPlugin.ConfigurationJson</c> and writes them once a runner has
/// accepted a change, so it is the only writer of
/// <c>%LOCALAPPDATA%\SRTHost\config\&lt;pluginId&gt;.json</c>. The plugin-side
/// <c>SRTPluginBase.PluginConfigurationStore</c> reads the same file and deliberately does not write
/// it: two processes replacing one path through <see cref="File.Replace(string, string, string)"/>
/// is a race whose loser is the user's settings.
/// <para>
/// Settings cross this boundary as text, never as an object. The host cannot deserialise them - the
/// type is in an assembly it never loads - and it does not need to: what it holds is the document
/// the plugin's own serialiser will read, and the form edits it as a <c>JsonNode</c> tree.
/// </para>
/// </remarks>
public static class PluginSettingsStore
{
    /// <summary>
    /// Reads a plugin's settings document, or null when it has never been configured.
    /// </summary>
    /// <remarks>
    /// An unreadable file is reported as null rather than thrown, because the caller is a plugin
    /// launch: starting with the plugin's own defaults and saying so in the log beats refusing to
    /// start it at all. A file that is present but malformed is a different matter and is left to the
    /// runner, which rejects it with <c>ConfigurationInvalid</c> and a message naming the problem.
    /// </remarks>
    public static string? Read(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        string path = HostPaths.ConfigFile(pluginId);

        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a plugin's settings document.
    /// </summary>
    /// <remarks>
    /// Through a temporary file and <see cref="File.Replace(string, string, string)"/>, so an
    /// interrupted save - a crash, the host being killed mid-write - leaves the previous settings
    /// intact rather than a truncated file that fails to parse on the next launch. Same shape as the
    /// plugin-side store, deliberately: they write the same file and it would be strange for one of
    /// them to be less careful.
    /// </remarks>
    public static void Write(string pluginId, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(json);

        string path = HostPaths.ConfigFile(pluginId);
        string temporary = path + ".tmp";

        Directory.CreateDirectory(HostPaths.ConfigDirectory);

        File.WriteAllText(temporary, json, Encoding.UTF8);

        if (File.Exists(path))
            File.Replace(temporary, path, destinationBackupFileName: null);
        else
            File.Move(temporary, path);
    }

    /// <summary>Deletes a plugin's settings, so its next start uses the plugin's own defaults.</summary>
    public static void Delete(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        string path = HostPaths.ConfigFile(pluginId);

        if (File.Exists(path))
            File.Delete(path);
    }
}
