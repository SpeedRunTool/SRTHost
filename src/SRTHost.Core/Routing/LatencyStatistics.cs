namespace SRTHost.Core.Routing;

/// <summary>A latency distribution, as reported to the UI and to the phase checkpoint.</summary>
/// <param name="Samples">How many samples the percentiles were computed from.</param>
/// <param name="Median">The 50th percentile.</param>
/// <param name="Percentile99">The 99th percentile - the number the design is gated on.</param>
/// <param name="Maximum">The worst sample in the window.</param>
public readonly record struct LatencySnapshot(int Samples, TimeSpan Median, TimeSpan Percentile99, TimeSpan Maximum)
{
    /// <summary>A short one-line rendering, for a log or a status bar.</summary>
    public override string ToString()
        => Samples == 0
            ? "no samples"
            : $"n={Samples}, p50={Median.TotalMilliseconds:F3} ms, p99={Percentile99.TotalMilliseconds:F3} ms, "
              + $"max={Maximum.TotalMilliseconds:F3} ms";
}

/// <summary>
/// A fixed-size window of latency samples, summarised on demand.
/// </summary>
/// <remarks>
/// A ring buffer rather than a growing list or a histogram. A list would grow without bound in a
/// process that runs for hours at 30 Hz per plugin, and a histogram would need its buckets chosen in
/// advance - which is exactly the thing not yet known here, since the whole point is to find out
/// what the distribution looks like.
/// <para>
/// The window also makes the number mean the right thing: a p99 over the last few thousand frames
/// describes how the application is behaving <em>now</em>, whereas a p99 over the whole session is
/// dominated by whatever happened during startup and never recovers from one bad minute.
/// </para>
/// <para>
/// Recording is a wrapping increment and a store, with no lock. Two writers can overwrite each
/// other's slot under contention; that is deliberately accepted, because the alternative is putting
/// a lock on the delivery hot path in order to improve the precision of a diagnostic.
/// </para>
/// </remarks>
public sealed class LatencyStatistics(int windowSize = 4096)
{
    private readonly long[] samples = new long[windowSize];

    private long written;

    /// <summary>Records one sample.</summary>
    public void Record(TimeSpan latency)
    {
        long index = Interlocked.Increment(ref written) - 1;

        // Negative latency is possible and is not a bug worth propagating: the producer's timestamp
        // comes from another process, and DateTime.UtcNow can step backwards across a clock
        // adjustment. Clamping keeps one NTP correction from making a percentile meaningless.
        samples[(int)(index % samples.Length)] = Math.Max(latency.Ticks, 0);
    }

    /// <summary>Summarises the current window.</summary>
    public LatencySnapshot Snapshot()
    {
        long total = Interlocked.Read(ref written);

        if (total == 0)
            return new LatencySnapshot(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        int count = (int)Math.Min(total, samples.Length);
        long[] window = new long[count];

        Array.Copy(samples, window, count);
        Array.Sort(window);

        return new LatencySnapshot(
            count,
            TimeSpan.FromTicks(window[Percentile(count, 0.50)]),
            TimeSpan.FromTicks(window[Percentile(count, 0.99)]),
            TimeSpan.FromTicks(window[count - 1]));
    }

    /// <summary>Forgets every sample, so a measurement can start from a known point.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref written, 0);
        Array.Clear(samples);
    }

    private static int Percentile(int count, double fraction)
        => Math.Clamp((int)Math.Ceiling(count * fraction) - 1, 0, count - 1);
}
