using System.Buffers;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Routing;

/// <summary>
/// One published payload on its way from a producer to N consumers, in a pooled buffer with a
/// reference count.
/// </summary>
/// <remarks>
/// A payload arrives in the frame reader's own buffer, which is valid only for the duration of the
/// handler call - so the router has to copy it before fanning it out. Copying it once per subscriber
/// would be the obvious thing and is wrong twice over: at 30 Hz with several consumers it allocates
/// steadily enough to matter, and it means the same bytes exist several times for no reason.
/// <para>
/// So: one rent, one copy, and a count of how many queues still hold it. The buffer goes back to the
/// pool when the last of them is done - which includes the ones that <em>dropped</em> it, because a
/// bounded queue discarding a stale frame is the normal case here, not an error path. A drop that
/// forgot to release would leak a pooled array per drop, and drops are the thing this design does
/// deliberately and often.
/// </para>
/// </remarks>
internal sealed class PooledFrame
{
    private byte[] buffer;
    private int references;

    private PooledFrame(byte[] buffer, int length, DataFrameHeader header, PayloadCodec codec, int references)
    {
        this.buffer = buffer;
        this.references = references;

        Length = length;
        Header = header;
        Codec = codec;
    }

    /// <summary>The preamble the payload arrived with, forwarded unchanged.</summary>
    public DataFrameHeader Header { get; }

    /// <summary>How the payload is encoded. The router does not decode it; consumers need it.</summary>
    public PayloadCodec Codec { get; }

    /// <summary>Payload length in bytes.</summary>
    public int Length { get; }

    /// <summary>The payload. Valid until the last holder releases it.</summary>
    public ReadOnlyMemory<byte> Payload => buffer.AsMemory(0, Length);

    /// <summary>
    /// Copies <paramref name="payload"/> into a pooled buffer held on behalf of
    /// <paramref name="subscribers"/> holders.
    /// </summary>
    public static PooledFrame Copy(
        in DataFrameHeader header,
        in ReadOnlySequence<byte> payload,
        PayloadCodec codec,
        int subscribers)
    {
        int length = checked((int)payload.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);

        payload.CopyTo(buffer);

        return new PooledFrame(buffer, length, header, codec, subscribers);
    }

    /// <summary>Gives up one holder's claim, returning the buffer to the pool at zero.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref references) != 0)
            return;

        // Exchanged rather than simply returned, so a release-after-free hands out a null reference
        // at the point of the bug instead of a buffer that some other part of the process is now
        // writing into. Use-after-return is the failure mode pooling exists to trade for, and it is
        // silent by nature.
        byte[] released = Interlocked.Exchange(ref buffer, [])!;

        if (released.Length != 0)
            ArrayPool<byte>.Shared.Return(released);
    }
}
