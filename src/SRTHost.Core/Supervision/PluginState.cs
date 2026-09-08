using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Supervision;

/// <summary>What the host should do when a plugin's runner dies.</summary>
public enum RestartPolicy
{
    /// <summary>Restart whenever the runner stops for any reason other than the host asking it to.</summary>
    Always,

    /// <summary>Restart only after an abnormal exit. The default.</summary>
    OnCrash,

    /// <summary>Never restart; leave it stopped for the user to deal with.</summary>
    Never,
}

/// <summary>
/// Everything the host knows about one plugin right now.
/// </summary>
/// <remarks>
/// A record, held in a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// keyed by plugin id and replaced wholesale on every change. It replaces generation 1's
/// <c>PluginStateValue&lt;T&gt;</c>, which was used as a dictionary key while overriding neither
/// <see cref="object.Equals(object)"/> nor <see cref="object.GetHashCode"/> - so two states
/// describing the same plugin were different keys, and a lookup could silently miss. Records get
/// both for free, and using the id as the key means identity never depends on the value at all.
/// </remarks>
public sealed record PluginState
{
    /// <summary>The plugin this describes.</summary>
    public required string PluginId { get; init; }

    /// <summary>Where it is in its lifecycle.</summary>
    public PluginStatus Status { get; init; } = PluginStatus.NotLoaded;

    /// <summary>Why, when the status is a failure.</summary>
    public PluginSubStatus SubStatus { get; init; } = PluginSubStatus.None;

    /// <summary>Human-readable detail for the UI and the log.</summary>
    public string? Detail { get; init; }

    /// <summary>Process id of the runner hosting it, when one is running.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Which runner architecture it was given.</summary>
    public PluginArchitecture Architecture { get; init; }

    /// <summary>When the runner last said anything at all.</summary>
    public DateTimeOffset? LastHeartbeat { get; init; }

    /// <summary>How many times it has been restarted inside the current window.</summary>
    public int RestartCount { get; init; }

    /// <summary>Whether the producer's source - usually the game process - is currently available.</summary>
    public bool SourceAvailable { get; init; }
}
