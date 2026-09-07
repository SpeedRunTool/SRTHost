using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using SRTHost.Ipc;

namespace SRTHost.Ipc.Tests;

/// <summary>
/// Measures the frame codec over a real loopback pipe, against the Phase 2 targets: more than
/// 50,000 frames a second, and a 99th-percentile delivery latency under 200 microseconds for a 10 KB
/// frame.
/// </summary>
/// <remarks>
/// The numbers are reported on every run; the assertions are deliberately far looser than the
/// targets. A throughput test that asserts its real target is a test that fails on a busy build
/// agent, and a suite people learn to re-run is worth less than no suite at all. The loose bound
/// still catches what actually matters - an accidental copy per frame, a flush per byte, a lock held
/// across a read - because those cost an order of magnitude, not a few percent.
/// <para>
/// Read the reported figures rather than the pass/fail when judging whether the phase target is met.
/// </para>
/// </remarks>
public class ThroughputTests(ITestOutputHelper output)
{
    private const int PayloadBytes = 10 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Catastrophic-regression floor, not the phase target. See the class remarks.</summary>
    private const double MinimumFramesPerSecond = 20_000;

    /// <summary>Catastrophic-regression ceiling, not the phase target. See the class remarks.</summary>
    private static readonly TimeSpan MaximumP99Latency = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task SustainsThroughputOverALoopbackPipe()
    {
        const int Frames = 20_000;

        byte[] payload = new byte[PayloadBytes];
        Random.Shared.NextBytes(payload);

        int received = 0;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using PipePair pair = await PipePair.CreateAsync(
            onData: (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref received) == Frames)
                    done.TrySetResult();

                return ValueTask.CompletedTask;
            });

        DataFrameHeader header = new()
        {
            Sequence = 0,
            TimestampUtcTicks = 0,
            ChannelId = "srt/demo/values",
        };

        long start = Stopwatch.GetTimestamp();

        for (int i = 0; i < Frames; i++)
        {
            await pair.Client.SendDataAsync(
                header with { Sequence = (ulong)i },
                new ReadOnlySequence<byte>(payload),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await done.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        double framesPerSecond = Frames / elapsed.TotalSeconds;
        double megabytesPerSecond = framesPerSecond * PayloadBytes / (1024 * 1024);

        output.WriteLine(
            $"{Frames:N0} frames of {PayloadBytes:N0} bytes in {elapsed.TotalMilliseconds:N0} ms "
            + $"= {framesPerSecond:N0} frames/s ({megabytesPerSecond:N0} MiB/s). Phase target: 50,000 frames/s.");

        Assert.Equal(Frames, received);
        Assert.True(
            framesPerSecond > MinimumFramesPerSecond,
            $"Throughput collapsed to {framesPerSecond:N0} frames/s, under the {MinimumFramesPerSecond:N0} floor.");
    }

    /// <summary>
    /// Latency is measured at a paced rate, not at saturation. Blasting frames as fast as they will
    /// go measures how deep the queue got, which is a throughput number wearing a latency costume;
    /// what matters for an overlay is how long a frame takes when the link is not already full.
    /// </summary>
    [Fact]
    public async Task DeliversAFrameWithLowLatency()
    {
        const int Samples = 2_000;

        byte[] payload = new byte[PayloadBytes];
        Random.Shared.NextBytes(payload);

        long[] latencyTicks = new long[Samples];
        int index = 0;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using PipePair pair = await PipePair.CreateAsync(
            onData: (header, _, _, _) =>
            {
                long elapsed = DateTime.UtcNow.Ticks - header.TimestampUtcTicks;
                int slot = Interlocked.Increment(ref index) - 1;

                if (slot < Samples)
                    latencyTicks[slot] = elapsed;

                if (slot == Samples - 1)
                    done.TrySetResult();

                return ValueTask.CompletedTask;
            });

        for (int i = 0; i < Samples; i++)
        {
            await pair.Client.SendDataAsync(
                new DataFrameHeader
                {
                    Sequence = (ulong)i,
                    TimestampUtcTicks = DateTime.UtcNow.Ticks,
                    ChannelId = "srt/demo/values",
                },
                new ReadOnlySequence<byte>(payload),
                cancellationToken: TestContext.Current.CancellationToken);

            // ~1 kHz, well above the 30 Hz a producer actually ticks at, while still leaving the
            // link idle between frames.
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await done.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Array.Sort(latencyTicks);

        TimeSpan median = TimeSpan.FromTicks(latencyTicks[Samples / 2]);
        TimeSpan p99 = TimeSpan.FromTicks(latencyTicks[(int)(Samples * 0.99)]);
        TimeSpan worst = TimeSpan.FromTicks(latencyTicks[^1]);

        output.WriteLine(
            $"One-way latency over {Samples:N0} paced {PayloadBytes:N0} byte frames: "
            + $"median {median.TotalMicroseconds:N1} us, p99 {p99.TotalMicroseconds:N1} us, "
            + $"max {worst.TotalMicroseconds:N1} us. Phase target: p99 under 200 us.");

        Assert.True(
            p99 < MaximumP99Latency,
            $"p99 latency was {p99.TotalMilliseconds:N1} ms, over the {MaximumP99Latency.TotalMilliseconds:N0} ms ceiling.");
    }

    private sealed class PipePair : IAsyncDisposable
    {
        private readonly List<Task> loops = [];

        public required IpcConnection Client { get; init; }

        public required IpcConnection Server { get; init; }

        public static async Task<PipePair> CreateAsync(DataFrameHandler onData)
        {
            string name = IpcProtocol.PipeName(IpcProtocol.NewSessionId(), "SpeedRunTool.Demo.Producer");

            NamedPipeServerStream serverPipe = IpcPipe.CreateServer(name);
            Task waiting = serverPipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            NamedPipeClientStream clientPipe = await IpcPipe.ConnectAsync(name, Timeout, TestContext.Current.CancellationToken);
            await waiting.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            IpcConnection server = new(serverPipe) { OnDataFrame = onData };
            IpcConnection client = new(clientPipe);

            PipePair pair = new() { Client = client, Server = server };
            pair.loops.Add(Task.Run(() => Swallow(server.RunAsync(CancellationToken.None))));
            pair.loops.Add(Task.Run(() => Swallow(client.RunAsync(CancellationToken.None))));

            return pair;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            await Task.WhenAll(loops);
        }

        private static async Task Swallow(Task loop)
        {
            try
            {
                await loop;
            }
            catch
            {
                // A read loop always ends in an exception at teardown.
            }
        }
    }
}
