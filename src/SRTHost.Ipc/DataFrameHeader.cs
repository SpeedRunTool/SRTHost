using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SRTHost.Ipc;

/// <summary>
/// The small binary preamble on every <see cref="IpcChannel.Data"/> frame, ahead of the opaque
/// payload.
/// </summary>
/// <remarks>
/// The router has to know which channel a payload belongs to and in what order payloads arrived,
/// but it must never decode the payload itself - that is the property keeping the router one AnyCPU
/// process while its plugins are x86 or x64. So the metadata it needs is here, in a fixed binary
/// preamble it can read, and everything past it is bytes it forwards untouched.
/// <code>
/// off  size  field
///  0    8    sequence            uint64, monotonic per channel
///  8    8    timestampUtcTicks   int64, DateTime.UtcNow.Ticks at publish
/// 16    2    channelIdLength     uint16, bytes of UTF-8 that follow
/// 18    n    channelId           UTF-8
/// 18+n  ...  payload             opaque
/// </code>
/// <para>
/// The channel id is repeated per frame rather than exchanged once for a numeric handle. At 30 Hz a
/// twenty-byte string is nothing, and it keeps the frame self-describing: a hex dump of the pipe
/// says which channel it belongs to, with no handshake state to reconstruct. Swap it for a handle if
/// a profile ever says otherwise.
/// </para>
/// <para>
/// The timestamp is what P4 measures router overhead with. <c>DateTime.UtcNow</c> resolves well
/// below a microsecond on Windows 8 and later and is comparable across processes on one machine,
/// which <c>Stopwatch</c> ticks are not.
/// </para>
/// </remarks>
public readonly record struct DataFrameHeader
{
    /// <summary>Size of the fixed portion, before the channel id.</summary>
    public const int FixedSize = 18;

    /// <summary>Longest channel id accepted, in UTF-8 bytes.</summary>
    public const int MaxChannelIdLength = 512;

    /// <summary>Monotonically increasing per channel, so a consumer can spot a dropped frame.</summary>
    public required ulong Sequence { get; init; }

    /// <summary>When the producer published this, as <see cref="DateTime.UtcNow"/> ticks.</summary>
    public required long TimestampUtcTicks { get; init; }

    /// <summary>The channel this payload belongs to.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Total encoded size of this preamble for the current channel id.</summary>
    public int EncodedSize => FixedSize + Encoding.UTF8.GetByteCount(ChannelId);

    /// <summary>Writes the preamble into <paramref name="destination"/>.</summary>
    /// <returns>How many bytes were written.</returns>
    public int Write(Span<byte> destination)
    {
        int channelIdLength = Encoding.UTF8.GetByteCount(ChannelId);

        if (channelIdLength > MaxChannelIdLength)
            throw new IpcProtocolException($"Channel id is {channelIdLength} bytes, over the {MaxChannelIdLength} byte limit.");

        int total = FixedSize + channelIdLength;

        if (destination.Length < total)
            throw new ArgumentException($"Need {total} bytes for this data frame header.", nameof(destination));

        BinaryPrimitives.WriteUInt64LittleEndian(destination[..8], Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..16], TimestampUtcTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..18], (ushort)channelIdLength);
        Encoding.UTF8.GetBytes(ChannelId, destination[FixedSize..total]);

        return total;
    }

    /// <summary>
    /// Reads the preamble from the front of <paramref name="frame"/> and hands back the payload
    /// behind it.
    /// </summary>
    /// <exception cref="IpcProtocolException">The frame is too short, or the channel id is oversize.</exception>
    public static DataFrameHeader Read(in ReadOnlySequence<byte> frame, out ReadOnlySequence<byte> payload)
    {
        if (frame.Length < FixedSize)
            throw new IpcProtocolException($"Data frame is {frame.Length} bytes, shorter than its {FixedSize} byte header.");

        Span<byte> fixedPart = stackalloc byte[FixedSize];
        frame.Slice(0, FixedSize).CopyTo(fixedPart);

        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(fixedPart[..8]);
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(fixedPart[8..16]);
        int channelIdLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[16..18]);

        if (channelIdLength > MaxChannelIdLength)
            throw new IpcProtocolException($"Data frame declares a {channelIdLength} byte channel id, over the {MaxChannelIdLength} byte limit.");

        long total = FixedSize + (long)channelIdLength;

        if (frame.Length < total)
            throw new IpcProtocolException($"Data frame is {frame.Length} bytes but declares a {channelIdLength} byte channel id.");

        ReadOnlySequence<byte> channelIdBytes = frame.Slice(FixedSize, channelIdLength);
        string channelId = channelIdBytes.IsSingleSegment
            ? Encoding.UTF8.GetString(channelIdBytes.FirstSpan)
            : Encoding.UTF8.GetString(channelIdBytes.ToArray());

        payload = frame.Slice(total);

        return new DataFrameHeader
        {
            Sequence = sequence,
            TimestampUtcTicks = timestamp,
            ChannelId = channelId,
        };
    }
}
