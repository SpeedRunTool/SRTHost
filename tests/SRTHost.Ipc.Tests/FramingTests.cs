using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Ipc.Tests;

/// <summary>
/// Covers the frame codec: what survives a round trip, and what is rejected.
/// </summary>
/// <remarks>
/// Framing is the one layer with no room to be approximately right. A byte stream carries no
/// resynchronisation marker, so a single mis-sized frame does not corrupt one message - it shifts
/// every header that follows into the middle of a payload, and the connection produces plausible
/// nonsense from then on. These tests exist to make that failure impossible to introduce quietly.
/// </remarks>
public class FramingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RoundTripsEveryHeaderFieldAndThePayload()
    {
        byte[] payload = [1, 2, 3, 4, 5, 250, 251, 252];

        FrameHeader sent = new()
        {
            Channel = IpcChannel.Control,
            Codec = PayloadCodec.Json,
            MessageKind = ControlMessageKind.LoadPlugin,
            CorrelationId = 0xDEADBEEF,
        };

        (FrameHeader header, byte[] body) = Assert.Single(await RoundTripAsync([(sent, payload)]));

        Assert.Equal(IpcChannel.Control, header.Channel);
        Assert.Equal(PayloadCodec.Json, header.Codec);
        Assert.Equal(ControlMessageKind.LoadPlugin, header.MessageKind);
        Assert.Equal(0xDEADBEEFu, header.CorrelationId);
        Assert.Equal(payload.Length, header.PayloadLength);
        Assert.Equal(payload, body);
    }

    [Fact]
    public async Task RoundTripsAnEmptyPayload()
    {
        (FrameHeader header, byte[] body) = Assert.Single(
            await RoundTripAsync([(new FrameHeader { Channel = IpcChannel.Control, MessageKind = ControlMessageKind.Ping }, [])]));

        Assert.Equal(0, header.PayloadLength);
        Assert.Empty(body);
        Assert.Equal(ControlMessageKind.Ping, header.MessageKind);
    }

    [Fact]
    public async Task KeepsManyFramesInOrderAndSeparate()
    {
        (FrameHeader Header, byte[] Payload)[] sent = [.. Enumerable.Range(0, 200).Select(i =>
            (new FrameHeader { Channel = IpcChannel.Data, CorrelationId = (uint)i }, new byte[i % 97]))];

        for (int i = 0; i < sent.Length; i++)
            Array.Fill(sent[i].Payload, (byte)i);

        IReadOnlyList<(FrameHeader Header, byte[] Payload)> received = await RoundTripAsync(sent);

        Assert.Equal(sent.Length, received.Count);

        for (int i = 0; i < sent.Length; i++)
        {
            Assert.Equal((uint)i, received[i].Header.CorrelationId);
            Assert.Equal(sent[i].Payload, received[i].Payload);
        }
    }

    /// <summary>
    /// A pipe hands over whatever has arrived, not whatever is wanted - so every frame boundary has
    /// to survive the header itself being split across reads.
    /// </summary>
    [Fact]
    public async Task ReassemblesFramesDeliveredOneByteAtATime()
    {
        byte[] wire = await EncodeAsync(
        [
            (new FrameHeader { Channel = IpcChannel.Control, MessageKind = ControlMessageKind.Hello }, [9, 8, 7]),
            (new FrameHeader { Channel = IpcChannel.Data, CorrelationId = 42 }, [.. Enumerable.Range(0, 300).Select(i => (byte)i)]),
        ]);

        Pipe pipe = new();
        List<(FrameHeader Header, byte[] Payload)> received = [];

        Task reading = new FrameReader(pipe.Reader).ReadAllAsync(
            (header, payload, _) =>
            {
                received.Add((header, payload.ToArray()));
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        foreach (byte b in wire)
        {
            pipe.Writer.Write([b]);
            await pipe.Writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        await pipe.Writer.CompleteAsync();
        await reading.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(2, received.Count);
        Assert.Equal([9, 8, 7], received[0].Payload);
        Assert.Equal(300, received[1].Payload.Length);
        Assert.Equal(42u, received[1].Header.CorrelationId);
    }

    [Fact]
    public async Task RejectsAPayloadLengthOverTheLimit()
    {
        byte[] header = new byte[FrameHeader.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(header, FrameHeader.MaxPayloadLength + 1u);

        IpcProtocolException error = await Assert.ThrowsAsync<IpcProtocolException>(() => ReadRawAsync(header));
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The length is read from an untrusted peer and decides how much the reader will buffer, so it
    /// has to be rejected before it is acted on rather than after.
    /// </summary>
    [Fact]
    public async Task RejectsAnOversizeLengthWithoutWaitingForTheBytes()
    {
        byte[] header = new byte[FrameHeader.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);

        // Nothing follows the header, and the stream is never completed. If the reader were waiting
        // for 4 GiB before validating, this would hang rather than throw.
        await Assert.ThrowsAsync<IpcProtocolException>(() => ReadRawAsync(header, completeWriter: false));
    }

    [Fact]
    public async Task RejectsANonZeroReservedField()
    {
        byte[] header = new byte[FrameHeader.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 1u);

        IpcProtocolException error = await Assert.ThrowsAsync<IpcProtocolException>(() => ReadRawAsync(header));
        Assert.Contains("reserved", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAnUnknownChannel()
    {
        byte[] header = new byte[FrameHeader.Size];
        header[4] = 200;

        await Assert.ThrowsAsync<IpcProtocolException>(() => ReadRawAsync(header));
    }

    [Fact]
    public async Task RejectsAnUnknownCodec()
    {
        byte[] header = new byte[FrameHeader.Size];
        header[5] = 200;

        await Assert.ThrowsAsync<IpcProtocolException>(() => ReadRawAsync(header));
    }

    /// <summary>
    /// A half-written frame means the peer died mid-send. Reporting it beats silently dropping the
    /// remainder, because the supervisor's response - restart the runner - depends on knowing.
    /// </summary>
    [Fact]
    public async Task RejectsAStreamThatEndsInsideAFrame()
    {
        byte[] wire = await EncodeAsync([(new FrameHeader { Channel = IpcChannel.Data }, [1, 2, 3, 4, 5, 6, 7, 8])]);

        IpcProtocolException error = await Assert.ThrowsAsync<IpcProtocolException>(
            () => ReadRawAsync(wire[..^3]));

        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The writer stamps the length from the bytes it was handed, so a caller cannot desynchronise
    /// the stream by supplying one that disagrees.
    /// </summary>
    [Fact]
    public async Task IgnoresAPayloadLengthSuppliedByTheCaller()
    {
        FrameHeader lying = new() { Channel = IpcChannel.Data, PayloadLength = 99_999 };

        (FrameHeader header, byte[] body) = Assert.Single(await RoundTripAsync([(lying, [1, 2, 3])]));

        Assert.Equal(3, header.PayloadLength);
        Assert.Equal([1, 2, 3], body);
    }

    /// <summary>
    /// Two frames interleaved halfway through are indistinguishable from one corrupt frame, and there
    /// is no point in the stream at which a reader could recover.
    /// </summary>
    [Fact]
    public async Task SerialisesConcurrentWrites()
    {
        const int Writers = 16;
        const int PerWriter = 50;

        Pipe pipe = new();
        using FrameWriter writer = new(pipe.Writer);
        List<(FrameHeader Header, byte[] Payload)> received = [];

        Task reading = new FrameReader(pipe.Reader).ReadAllAsync(
            (header, payload, _) =>
            {
                received.Add((header, payload.ToArray()));
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            byte[] payload = new byte[512];
            Array.Fill(payload, (byte)w);

            for (int i = 0; i < PerWriter; i++)
            {
                await writer.WriteAsync(
                    new FrameHeader { Channel = IpcChannel.Data, CorrelationId = (uint)w },
                    payload,
                    TestContext.Current.CancellationToken);
            }
        })));

        await pipe.Writer.CompleteAsync();
        await reading.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(Writers * PerWriter, received.Count);

        // Every frame must be wholly one writer's: same byte throughout, matching its header.
        foreach ((FrameHeader header, byte[] payload) in received)
        {
            Assert.Equal(512, payload.Length);
            Assert.All(payload, b => Assert.Equal((byte)header.CorrelationId, b));
        }
    }

    [Fact]
    public async Task RefusesToWriteAnOversizePayload()
    {
        Pipe pipe = new();
        using FrameWriter writer = new(pipe.Writer);

        await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await writer.WriteAsync(
                new FrameHeader { Channel = IpcChannel.Data },
                new byte[FrameHeader.MaxPayloadLength + 1],
                TestContext.Current.CancellationToken));
    }

    #region Helpers

    private static async Task<byte[]> EncodeAsync(IEnumerable<(FrameHeader Header, byte[] Payload)> frames)
    {
        Pipe pipe = new();
        using (FrameWriter writer = new(pipe.Writer))
        {
            foreach ((FrameHeader header, byte[] payload) in frames)
                await writer.WriteAsync(header, payload, TestContext.Current.CancellationToken);
        }

        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAtLeastAsync(int.MaxValue, TestContext.Current.CancellationToken);
        return result.Buffer.ToArray();
    }

    private static async Task<IReadOnlyList<(FrameHeader Header, byte[] Payload)>> RoundTripAsync(
        IEnumerable<(FrameHeader Header, byte[] Payload)> frames)
    {
        Pipe pipe = new();
        List<(FrameHeader, byte[])> received = [];

        Task reading = new FrameReader(pipe.Reader).ReadAllAsync(
            (header, payload, _) =>
            {
                received.Add((header, payload.ToArray()));
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        using (FrameWriter writer = new(pipe.Writer))
        {
            foreach ((FrameHeader header, byte[] payload) in frames)
                await writer.WriteAsync(header, payload, TestContext.Current.CancellationToken);
        }

        await pipe.Writer.CompleteAsync();
        await reading.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        return received;
    }

    private static async Task ReadRawAsync(byte[] wire, bool completeWriter = true)
    {
        Pipe pipe = new();

        await pipe.Writer.WriteAsync(wire, TestContext.Current.CancellationToken);

        if (completeWriter)
            await pipe.Writer.CompleteAsync();

        Task reading = new FrameReader(pipe.Reader).ReadAllAsync(
            (_, _, _) => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken);

        await reading.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    #endregion
}
