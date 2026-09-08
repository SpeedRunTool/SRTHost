namespace SRTHost.Core.Logging;

/// <summary>
/// A bounded, thread-safe ring of the most recent log entries.
/// </summary>
/// <remarks>
/// The host and every runner write into this concurrently - the supervisor re-emits each forwarded
/// record on whichever pipe read loop delivered it - while the UI thread reads it to render. That is
/// the whole reason this type exists rather than a <c>List&lt;LogEntry&gt;</c>: <c>Issue/35</c>'s
/// file logger was not thread-safe, and a log viewer over an unsynchronised list is a
/// <see cref="InvalidOperationException"/> waiting for the first plugin crash, which is exactly when
/// somebody is looking at it.
/// <para>
/// A fixed array with a write cursor rather than a <c>Queue</c>: the capacity is reached within
/// minutes of normal running and stays reached, so a structure that trims is a structure that trims
/// on every single write. Overwriting in place allocates nothing after construction.
/// </para>
/// <para>
/// Appending raises <see cref="Appended"/> synchronously on the writer's thread. Subscribers must
/// marshal to their own thread themselves; the ring deliberately knows nothing about
/// <c>Dispatcher</c> so that it stays testable and usable headless.
/// </para>
/// </remarks>
public sealed class LogRing(int capacity = LogRing.DefaultCapacity)
{
    /// <summary>Entries kept before the oldest is overwritten.</summary>
    /// <remarks>
    /// Ten thousand: enough to hold a crash and the minutes around it, small enough that filtering
    /// the whole ring on a keystroke stays imperceptible. Anything older belongs in the log file,
    /// which is why there is one.
    /// </remarks>
    public const int DefaultCapacity = 10_000;

    private readonly Lock gate = new();
    private readonly LogEntry[] entries = new LogEntry[
        capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];

    private int count;
    private int next;

    /// <summary>Raised for each appended entry, on the appending thread.</summary>
    public event Action<LogEntry>? Appended;

    /// <summary>How many entries are currently held.</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return count;
        }
    }

    /// <summary>The capacity this ring was created with.</summary>
    public int Capacity => entries.Length;

    /// <summary>How many entries have been dropped off the back since the last <see cref="Clear"/>.</summary>
    /// <remarks>
    /// Surfaced so the log view can say "showing the last 10,000 of 41,332 lines" rather than
    /// implying it is showing everything. A viewer that silently discards is a viewer people stop
    /// trusting.
    /// </remarks>
    public long Overwritten { get; private set; }

    /// <summary>Appends one entry, overwriting the oldest if the ring is full.</summary>
    public void Append(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (gate)
        {
            if (entries[next] is not null)
                Overwritten++;

            entries[next] = entry;
            next = (next + 1) % entries.Length;

            if (count < entries.Length)
                count++;
        }

        Appended?.Invoke(entry);
    }

    /// <summary>Everything currently held, oldest first.</summary>
    /// <remarks>
    /// Returns a copy taken under the lock. A lazily-enumerated view would let the ring be
    /// overwritten mid-enumeration by a runner that is logging at thirty hertz, and the caller is a
    /// UI that is about to materialise it anyway.
    /// </remarks>
    public LogEntry[] Snapshot()
    {
        lock (gate)
        {
            LogEntry[] snapshot = new LogEntry[count];
            int start = count < entries.Length ? 0 : next;

            for (int index = 0; index < count; index++)
                snapshot[index] = entries[(start + index) % entries.Length];

            return snapshot;
        }
    }

    /// <summary>Empties the ring.</summary>
    public void Clear()
    {
        lock (gate)
        {
            Array.Clear(entries);
            count = 0;
            next = 0;
            Overwritten = 0;
        }
    }
}
