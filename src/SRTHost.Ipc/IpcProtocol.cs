using System.Text.RegularExpressions;

namespace SRTHost.Ipc;

/// <summary>
/// Protocol-wide constants and the pipe naming scheme.
/// </summary>
public static partial class IpcProtocol
{
    /// <summary>
    /// Version of the frame and message protocol, exchanged in the opening handshake.
    /// </summary>
    /// <remarks>
    /// Separate from the contract generation on purpose. The contract generation is about what
    /// plugins were compiled against; this is about what two SRT Host processes say to each other,
    /// and the two can move independently - an in-place update leaves a running router and a freshly
    /// spawned runner at different builds for the length of a restart.
    /// </remarks>
    public const int Version = 1;

    /// <summary>Prefix every SRT Host pipe name carries.</summary>
    public const string PipeNamePrefix = "SRTHost";

    /// <summary>
    /// Longest pipe name this will produce. Windows allows 256 characters in the pipe namespace; the
    /// margin is left because the name is composed from a plugin id, which is author-supplied.
    /// </summary>
    public const int MaxPipeNameLength = 200;

    /// <summary>
    /// Builds the pipe name for one plugin: <c>SRTHost.{sessionId}.{pluginId}</c>.
    /// </summary>
    /// <remarks>
    /// One pipe per plugin rather than one shared name with N server instances. It costs a handle per
    /// plugin and buys two things: there is no connection race to resolve, because a runner connects
    /// to a name that identifies it rather than to an anonymous instance from a pool that then has to
    /// announce which worker it is; and <c>pipelist</c> shows at a glance which pipe belongs to which
    /// plugin, which is worth a great deal while debugging a process tree.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The plugin id is empty or carries characters that are not legal in a pipe name, or the result
    /// is too long.
    /// </exception>
    public static string PipeName(string sessionId, string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        // A pipe name may not contain a backslash - that separates the server and the pipe namespace -
        // and a plugin id reaches here from an author's assembly attribute. The manifest generator
        // rejects anything outside this set at build time (SRT1007), but a manifest can also arrive
        // beside a downloaded plugin without ever having passed through that generator.
        if (!SafeNameCharacters().IsMatch(sessionId) || !SafeNameCharacters().IsMatch(pluginId))
        {
            throw new ArgumentException(
                $"Pipe name components must be letters, digits, '.', '_' or '-'; got '{sessionId}' and '{pluginId}'.");
        }

        string name = $"{PipeNamePrefix}.{sessionId}.{pluginId}";

        if (name.Length > MaxPipeNameLength)
        {
            throw new ArgumentException(
                $"Pipe name '{name}' is {name.Length} characters, over the {MaxPipeNameLength} limit.");
        }

        return name;
    }

    /// <summary>
    /// A per-launch session identifier, so two hosts running at once cannot collide on a pipe name.
    /// </summary>
    /// <remarks>
    /// Eight hex characters rather than a full GUID: it has to be read off a command line and matched
    /// against <c>pipelist</c> output by a human debugging a three-process application, and the
    /// collision it guards against is between the small number of hosts one user runs at once. A
    /// collision is not silent either - the server is created with
    /// <see cref="System.IO.Pipes.PipeOptions.FirstPipeInstance"/>, so a taken name fails loudly at
    /// startup instead of being shared.
    /// </remarks>
    public static string NewSessionId() => Guid.NewGuid().ToString("N")[..8];

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeNameCharacters();
}
