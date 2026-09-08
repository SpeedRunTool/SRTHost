using Microsoft.Extensions.Logging;
using SRTHost.Core.Logging;

namespace SRTHost.Core.Tests;

/// <summary>
/// The log ring, which is written from every runner's read loop at once and read by the UI thread.
/// </summary>
/// <remarks>
/// The concurrency test is the reason this file exists. Generation 1's file logger was not
/// thread-safe and the symptom - an exception inside the log viewer - only ever appeared when a
/// plugin was crashing, which is precisely when somebody is looking at the log.
/// </remarks>
public class LogRingTests
{
    [Fact]
    public void KeepsEntriesInOrder()
    {
        LogRing ring = new(capacity: 8);

        ring.Append(Entry("first"));
        ring.Append(Entry("second"));

        Assert.Equal(["first", "second"], ring.Snapshot().Select(entry => entry.Message));
    }

    [Fact]
    public void OverwritesTheOldestOnceFull()
    {
        LogRing ring = new(capacity: 3);

        foreach (int index in Enumerable.Range(1, 5))
            ring.Append(Entry(index.ToString()));

        Assert.Equal(["3", "4", "5"], ring.Snapshot().Select(entry => entry.Message));
        Assert.Equal(3, ring.Count);
        Assert.Equal(2, ring.Overwritten);
    }

    [Fact]
    public void RaisesAppendedForEachEntry()
    {
        LogRing ring = new(capacity: 4);
        List<string> seen = [];

        ring.Appended += entry => seen.Add(entry.Message);

        ring.Append(Entry("one"));
        ring.Append(Entry("two"));

        Assert.Equal(["one", "two"], seen);
    }

    [Fact]
    public void ClearEmptiesEverything()
    {
        LogRing ring = new(capacity: 2);

        ring.Append(Entry("a"));
        ring.Append(Entry("b"));
        ring.Append(Entry("c"));
        ring.Clear();

        Assert.Empty(ring.Snapshot());
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Overwritten);
    }

    /// <summary>
    /// Concurrent appends and snapshots must neither throw nor tear.
    /// </summary>
    /// <remarks>
    /// A snapshot taken while the ring is being overwritten is the case that matters: an
    /// implementation that enumerated the backing array lazily would hand the UI an entry that was
    /// replaced mid-render, or a null.
    /// </remarks>
    [Fact]
    public async Task SurvivesConcurrentWritersAndReaders()
    {
        LogRing ring = new(capacity: 64);

        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(2));

        Task[] writers = [.. Enumerable.Range(0, 4).Select(writer => Task.Run(
            () =>
            {
                for (int index = 0; index < 5_000; index++)
                    ring.Append(Entry($"{writer}:{index}"));
            },
            TestContext.Current.CancellationToken))];

        Task reader = Task.Run(
            () =>
            {
                while (!stopping.IsCancellationRequested && !writers.All(task => task.IsCompleted))
                {
                    foreach (LogEntry entry in ring.Snapshot())
                        Assert.NotNull(entry.Message);
                }
            },
            TestContext.Current.CancellationToken);

        await Task.WhenAll([.. writers, reader]);

        Assert.Equal(64, ring.Count);
    }

    [Fact]
    public void PluginIdIsSplitOutOfTheForwardedCategory()
    {
        Assert.Equal(
            "SpeedRunTool.Demo.Producer",
            LogEntry.PluginIdFromCategory("SRTHost.Plugin.SpeedRunTool.Demo.Producer"));

        Assert.Null(LogEntry.PluginIdFromCategory("SRTHost.Core.Routing.IpcRouter"));
    }

    /// <summary>The host's own categories collapse to the type name; a plugin's stays whole.</summary>
    [Fact]
    public void ShortCategoryPrefersThePluginId()
    {
        Assert.Equal("IpcRouter", Entry("x", "SRTHost.Core.Routing.IpcRouter").ShortCategory);

        Assert.Equal(
            "SpeedRunTool.Demo.Producer",
            Entry("x", "SRTHost.Plugin.SpeedRunTool.Demo.Producer").ShortCategory);
    }

    private static LogEntry Entry(string message, string category = "SRTHost.Test") => new()
    {
        Timestamp = DateTimeOffset.Now,
        Level = LogLevel.Information,
        Category = category,
        Message = message,
        PluginId = LogEntry.PluginIdFromCategory(category),
    };
}
