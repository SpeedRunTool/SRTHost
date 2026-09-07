using System.Buffers;
using System.IO.Pipelines;

namespace SRTHost.Ipc;

/// <summary>
/// Serialises frames onto a <see cref="PipeWriter"/>.
/// </summary>
/// <remarks>
/// A single lock serialises every write. It is not protecting shared state so much as the stream
/// itself: two frames interleaved halfway through would be indistinguishable from one corrupt frame,
/// and there is no resynchronisation point to recover at. Fan-out to N subscribers therefore queues
/// on this lock, which is the intended behaviour - the alternative, a writer per logical stream,
/// would need its own reassembly on the far side.
/// <para>
/// <see cref="FrameHeader.PayloadLength"/> is always stamped from the payload actually supplied, so a
/// caller cannot desynchronise the stream by miscounting.
/// </para>
/// </remarks>
public sealed class FrameWriter : IDisposable
{
    private readonly PipeWriter writer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    /// <summary>Creates a writer over <paramref name="writer"/>.</summary>
    public FrameWriter(PipeWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        this.writer = writer;
    }

    /// <summary>Writes one frame and flushes it.</summary>
    /// <exception cref="IpcProtocolException">The payload exceeds <see cref="FrameHeader.MaxPayloadLength"/>.</exception>
    public ValueTask WriteAsync(
        FrameHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
        => WriteAsync(header, new ReadOnlySequence<byte>(payload), cancellationToken);

    /// <summary>
    /// Writes one frame whose payload spans several buffer segments, and flushes it.
    /// </summary>
    /// <remarks>
    /// The sequence overload is the one that matters on the hot path: a payload arriving from a
    /// producer is already a <see cref="ReadOnlySequence{T}"/> owned by the read side, and fanning it
    /// out to subscribers this way never flattens it into a single array.
    /// </remarks>
    public async ValueTask WriteAsync(
        FrameHeader header,
        ReadOnlySequence<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (payload.Length > FrameHeader.MaxPayloadLength)
        {
            throw new IpcProtocolException(
                $"Refusing to write a {payload.Length:N0} byte payload, over the "
                + $"{FrameHeader.MaxPayloadLength:N0} byte limit.");
        }

        FrameHeader stamped = header with { PayloadLength = (int)payload.Length };

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            stamped.Write(writer.GetSpan(FrameHeader.Size));
            writer.Advance(FrameHeader.Size);

            foreach (ReadOnlyMemory<byte> segment in payload)
                writer.Write(segment.Span);

            FlushResult result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (result.IsCompleted)
                throw new IpcProtocolException("The peer closed the connection while a frame was being written.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        gate.Dispose();
    }
}
