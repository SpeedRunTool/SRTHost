using Microsoft.Extensions.Logging;

namespace SRTHost.Ipc;

/// <summary>
/// One log record forwarded from a runner to the host.
/// </summary>
/// <remarks>
/// Travels on <see cref="IpcChannel.Log"/> rather than as a control message, so log volume never
/// competes with lifecycle traffic for the control path's request correlation, and so a reader can
/// route it on the header byte alone.
/// <para>
/// The host re-emits these into its own <c>ILoggerFactory</c> under
/// <c>SRTHost.Plugin.&lt;pluginId&gt;</c>, which is what puts runner output and host output in one
/// file and one log view. It replaces generation 1's <c>IPluginHostDelegates.OutputMessage</c> and
/// <c>ExceptionMessage</c> entirely - a plugin now just takes an <see cref="ILogger"/> and the
/// plumbing is the host's problem.
/// </para>
/// </remarks>
public sealed record IpcLogRecord
{
    /// <summary>When the record was created, in the runner.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Severity.</summary>
    public required LogLevel Level { get; init; }

    /// <summary>The logger category inside the runner, e.g. the plugin's type name.</summary>
    public required string Category { get; init; }

    /// <summary>The formatted message.</summary>
    public required string Message { get; init; }

    /// <summary>Event id, when the caller supplied one.</summary>
    public int EventId { get; init; }

    /// <summary>Exception detail, already formatted - the type does not cross the boundary.</summary>
    public string? Exception { get; init; }
}
