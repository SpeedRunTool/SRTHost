namespace SRTHost.Core;

/// <summary>
/// Every directory the host reads or writes, in one place.
/// </summary>
/// <remarks>
/// Collected here because scattering them is what produced the bug this replaces: the previous
/// generation <em>created</em> the plugins directory under <see cref="AppContext.BaseDirectory"/>
/// and <em>enumerated</em> it from <see cref="Directory.GetCurrentDirectory"/>, so a host launched
/// from anywhere but its own folder created an empty directory and then reported no plugins - with
/// no error, because both operations succeeded.
/// <para>
/// The base directory is the right answer in every case. A shortcut, a scheduled task, an OBS
/// launch and a debugger all set the working directory differently, and none of them are wrong to;
/// the plugins are where the executable is.
/// </para>
/// </remarks>
public static class HostPaths
{
    /// <summary>Directory holding one subdirectory per installed plugin.</summary>
    public static string DefaultPluginsDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Root of everything the host writes: <c>%LOCALAPPDATA%\SRTHost</c>.</summary>
    /// <remarks>
    /// Outside the install directory on purpose. The install lives in <c>%LOCALAPPDATA%\SRTHost</c>
    /// too but under the installer's control, and an in-app plugin update replaces a plugin's whole
    /// directory - so anything written beside a DLL is destroyed by the next update. Generation 1
    /// wrote a <c>.cfg</c> next to the plugin for exactly that reason and lost settings on every
    /// upgrade.
    /// </remarks>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SRTHost");

    /// <summary>Per-plugin settings, as <c>config\&lt;pluginId&gt;.json</c>.</summary>
    public static string ConfigDirectory => Path.Combine(DataDirectory, "config");

    /// <summary>Per-plugin scratch space, as <c>state\&lt;pluginId&gt;\</c>.</summary>
    public static string StateDirectory => Path.Combine(DataDirectory, "state");

    /// <summary>Host and forwarded runner logs.</summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>The settings file for one plugin.</summary>
    public static string ConfigFile(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return Path.Combine(ConfigDirectory, pluginId + ".json");
    }

    /// <summary>
    /// The runner executable for <paramref name="architecture"/>, beside the host.
    /// </summary>
    /// <remarks>
    /// All three executables share one directory - that is the publish shape the installer and the
    /// release zip both want, and it is why the build copies the runners next to
    /// <c>SRTHost.exe</c> rather than leaving them in their own per-platform output folders.
    /// </remarks>
    public static string RunnerExecutable(SRTPluginBase.Abstractions.PluginArchitecture architecture)
    {
        // Any means "the host picks", and the host picks 64-bit: it is what the machine is, and a
        // 32-bit runner is only ever needed to match a 32-bit game.
        string suffix = architecture == SRTPluginBase.Abstractions.PluginArchitecture.X86 ? "32" : "64";

        return Path.Combine(AppContext.BaseDirectory, $"SRTHost.PluginRunner{suffix}.exe");
    }
}
