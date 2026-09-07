using Microsoft.Extensions.Logging;

namespace SRTHost.PluginRunner;

/// <summary>
/// Everything a runner process is told on its command line.
/// </summary>
/// <remarks>
/// Deliberately tiny. A runner is launched by the router, never by a person, and almost everything
/// about the plugin - its id, its directory, its entry type, its stored configuration - arrives in
/// <see cref="Ipc.LoadPluginMessage"/> instead, over a connection that is already established and
/// already correlated. Anything put on the command line is a second place for those values to live
/// and a second place for them to be wrong.
/// <para>
/// What is left here is only what has to be known <em>before</em> the connection exists: which pipe
/// to dial, how long to wait for it, and how much to log while doing so.
/// </para>
/// </remarks>
internal sealed record RunnerOptions
{
    /// <summary>How long to wait for the router's end of the pipe to accept a connection.</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The pipe name to connect to, as built by <see cref="Ipc.IpcProtocol.PipeName"/>.</summary>
    public required string PipeName { get; init; }

    /// <summary>How long to wait for that pipe.</summary>
    public TimeSpan ConnectTimeout { get; init; } = DefaultConnectTimeout;

    /// <summary>
    /// Lowest level forwarded to the host's log. Filtered in the runner rather than the router so a
    /// chatty plugin at Trace does not pay the pipe write.
    /// </summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Plugin directory, when the router chose to state it up front. Purely a diagnostic convenience
    /// - <see cref="Ipc.LoadPluginMessage.PluginDirectory"/> is authoritative and this is only used
    /// to make a runner's command line readable in Task Manager or Process Explorer.
    /// </summary>
    public string? PluginDirectory { get; init; }

    /// <summary>Plugin id, for the same reason as <see cref="PluginDirectory"/>.</summary>
    public string? PluginId { get; init; }

    /// <summary>What to print when the arguments do not parse.</summary>
    public const string Usage = """
        SRT Host Plugin Runner - hosts exactly one plugin, launched by SRTHost.exe.

          --pipe <name>              Required. Named pipe to connect to.
          --connect-timeout <secs>   Seconds to wait for that pipe. Default 15.
          --log-level <level>        Trace|Debug|Information|Warning|Error|Critical|None.
                                     Default Information.
          --plugin-dir <path>        Diagnostic only; LoadPlugin is authoritative.
          --plugin-id <id>           Diagnostic only; LoadPlugin is authoritative.

        Both --key value and --key=value are accepted.
        """;

    /// <summary>
    /// Parses a command line.
    /// </summary>
    /// <remarks>
    /// Both <c>--key value</c> and <c>--key=value</c> are accepted, because the two are impossible
    /// to remember apart and the cost of taking either is one line. Generation 1 took only
    /// <c>--Key=Value</c> and silently ignored anything else, so a mistyped argument looked like a
    /// setting that did not work.
    /// <para>
    /// An unknown argument is an error rather than something to ignore. The only caller is the
    /// router, so an argument it does not recognise means the two executables are out of step -
    /// which is worth failing on at startup instead of behaving subtly differently for a session.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when <paramref name="options"/> was produced.</returns>
    public static bool TryParse(string[] args, out RunnerOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        error = null;

        string? pipeName = null;
        string? pluginDirectory = null;
        string? pluginId = null;
        TimeSpan connectTimeout = DefaultConnectTimeout;
        LogLevel logLevel = LogLevel.Information;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{argument}'.";
                return false;
            }

            string key = argument[2..];
            string? value = null;

            int separator = key.IndexOf('=', StringComparison.Ordinal);

            if (separator >= 0)
            {
                value = key[(separator + 1)..];
                key = key[..separator];
            }

            switch (key.ToLowerInvariant())
            {
                case "pipe":
                    if (!TryTakeValue(args, ref index, ref value, key, out error))
                        return false;

                    pipeName = value;
                    break;

                case "plugin-dir":
                    if (!TryTakeValue(args, ref index, ref value, key, out error))
                        return false;

                    pluginDirectory = value;
                    break;

                case "plugin-id":
                    if (!TryTakeValue(args, ref index, ref value, key, out error))
                        return false;

                    pluginId = value;
                    break;

                case "connect-timeout":
                    if (!TryTakeValue(args, ref index, ref value, key, out error))
                        return false;

                    if (!double.TryParse(value, out double seconds) || seconds <= 0)
                    {
                        error = $"--connect-timeout expects a positive number of seconds, got '{value}'.";
                        return false;
                    }

                    connectTimeout = TimeSpan.FromSeconds(seconds);
                    break;

                case "log-level":
                    if (!TryTakeValue(args, ref index, ref value, key, out error))
                        return false;

                    if (!Enum.TryParse(value, ignoreCase: true, out logLevel))
                    {
                        error = $"--log-level expects a {nameof(LogLevel)} name, got '{value}'.";
                        return false;
                    }

                    break;

                default:
                    error = $"Unknown argument '--{key}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            error = "--pipe is required.";
            return false;
        }

        options = new RunnerOptions
        {
            PipeName = pipeName,
            ConnectTimeout = connectTimeout,
            LogLevel = logLevel,
            PluginDirectory = pluginDirectory,
            PluginId = pluginId,
        };

        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, ref string? value, string key, out string? error)
    {
        error = null;

        if (value is not null)
            return true;

        if (index + 1 >= args.Length)
        {
            error = $"--{key} expects a value.";
            return false;
        }

        value = args[++index];
        return true;
    }
}
