using System.Buffers;
using System.IO.Pipelines;

namespace SRTHost.Ipc;

/// <summary>
/// Handles one decoded frame.
/// </summary>
/// <remarks>
/// <paramref name="payload"/> points straight into the pipe's own buffers and is valid only until
/// the returned <see cref="ValueTask"/> completes. Anything the handler intends to keep - to queue,
/// to hand to another thread, to fan out after returning - must be copied first. That constraint is
/// the entire reason the read side is a callback rather than an <c>IAsyncEnumerable</c>: it puts the
/// lifetime somewhere the compiler and the reader of the code can both see it.
/// </remarks>
public delegate ValueTask FrameHandler(
    FrameHeader header,
    ReadOnlySequence<byte> payload,
    CancellationToken cancellationToken);

/// <summary>
/// Decodes frames from a <see cref="PipeReader"/>.
/// </summary>
public sealed class FrameReader
{
    private readonly PipeReader reader;

    /// <summary>Creates a reader over <paramref name="reader"/>.</summary>
    public FrameReader(PipeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
    }

    /// <summary>
    /// Reads frames until the peer completes the stream or <paramref name="cancellationToken"/> fires,
    /// invoking <paramref name="handler"/> for each.
    /// </summary>
    /// <exception cref="IpcProtocolException">
    /// A header was malformed, or the stream ended part-way through a frame.
    /// </exception>
    public async Task ReadAllAsync(FrameHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload))
                await handler(header, payload, cancellationToken).ConfigureAwait(false);

            // Consumed up to whatever is left over, examined all the way to the end: without the
            // second position a partial frame would make ReadAsync return the same bytes forever
            // rather than waiting for more.
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                if (!buffer.IsEmpty)
                {
                    throw new IpcProtocolException(
                        $"The connection ended {buffer.Length:N0} bytes into an incomplete frame.");
                }

                return;
            }
        }
    }

    /// <summary>
    /// Pulls one whole frame off the front of <paramref name="buffer"/>, or reports that more bytes
    /// are needed.
    /// </summary>
    /// <remarks>
    /// The header is re-parsed each time this is called for a frame whose payload has not fully
    /// arrived. That is sixteen bytes of work per read on a partial frame, and it buys not having to
    /// carry half-decoded state across reads - which is where framing code usually goes wrong.
    /// </remarks>
    private static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        out FrameHeader header,
        out ReadOnlySequence<byte> payload)
    {
        header = default;
        payload = default;

        if (buffer.Length < FrameHeader.Size)
            return false;

        header = FrameHeader.Read(buffer);

        long frameLength = FrameHeader.Size + (long)header.PayloadLength;

        if (buffer.Length < frameLength)
            return false;

        payload = buffer.Slice(FrameHeader.Size, header.PayloadLength);
        buffer = buffer.Slice(frameLength);
        return true;
    }
}
