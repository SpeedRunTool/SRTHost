using Microsoft.Extensions.Logging;

namespace SRTHost.Core.Logging;

/// <summary>
/// One log line, from the host or forwarded from a runner, as the UI wants it.
/// </summary>
/// <remarks>
/// A flattened record rather than the <see cref="ILogger"/> state it came from. The log view holds
/// ten thousand of these and filters over them on every keystroke, so everything it sorts or matches
/// on has to already be a field - reformatting a message per keystroke is how a log viewer becomes
/// the slowest part of an application that routes thirty frames a second.
/// <para>
/// <see cref="PluginId"/> is derived once here rather than by parsing <see cref="Category"/> at
/// display time, because the plugin filter is the one a person actually reaches for: the whole point
/// of forwarding runner logs into the host's own factory is being able to ask "what was
/// <c>SpeedRunTool.Demo.Producer</c> saying when it died".
/// </para>
/// </remarks>
public sealed record LogEntry
{
    /// <summary>The prefix the supervisor logs forwarded runner records under.</summary>
    /// <remarks>
    /// Must match the category <c>PluginRunnerProcess</c> creates for its plugin logger. It is a
    /// constant on this side because this is the side that has to take it apart again.
    /// </remarks>
    public const string PluginCategoryPrefix = "SRTHost.Plugin.";

    /// <summary>When the record was written.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Severity.</summary>
    public required LogLevel Level { get; init; }

    /// <summary>The logger category.</summary>
    public required string Category { get; init; }

    /// <summary>The formatted message.</summary>
    public required string Message { get; init; }

    /// <summary>Exception detail, already formatted.</summary>
    public string? Exception { get; init; }

    /// <summary>Which plugin this came from, or null for the host's own output.</summary>
    public string? PluginId { get; init; }

    /// <summary>The category with the namespace trimmed off, for a column that has to stay narrow.</summary>
    public string ShortCategory =>
        PluginId ?? Category[(Category.LastIndexOf('.') + 1)..];

    /// <summary>Splits a category into a plugin id, when it names one.</summary>
    public static string? PluginIdFromCategory(string category)
        => category.StartsWith(PluginCategoryPrefix, StringComparison.Ordinal)
            ? category[PluginCategoryPrefix.Length..]
            : null;

    /// <summary>One line, the way it is written to the log file and copied to the clipboard.</summary>
    public override string ToString()
        => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Category}: {Message}"
            + (Exception is null ? string.Empty : Environment.NewLine + Exception);
}
