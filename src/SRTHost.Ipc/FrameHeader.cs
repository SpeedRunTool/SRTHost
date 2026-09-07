using System.Buffers;
using System.Buffers.Binary;
using SRTPluginBase.Abstractions;

namespace SRTHost.Ipc;

/// <summary>Which of the three multiplexed streams a frame belongs to.</summary>
/// <remarks>
/// One pipe carries all three. Separating them in the header rather than on separate pipes keeps a
/// plugin to a single connection - so a dead plugin is exactly one broken pipe - while still letting
/// the reader route a frame without deserialising anything.
/// </remarks>
public enum IpcChannel : byte
{
    /// <summary>Lifecycle and request/response traffic. JSON, see <see cref="IpcMessage"/>.</summary>
    Control = 0,

    /// <summary>Payload traffic. Opaque bytes the router never decodes.</summary>
    Data = 1,

    /// <summary>Log records forwarded from a runner into the host's logger.</summary>
    Log = 2,
}

/// <summary>
/// The fixed 16-byte little-endian frame header.
/// </summary>
/// <remarks>
/// Little-endian is stated explicitly through <see cref="BinaryPrimitives"/> rather than left to
/// <c>MemoryMarshal</c> over the struct. Both ends are x86-family today, so the two are identical in
/// practice - but a wire format that silently depends on host endianness is the kind of thing that
/// is free to get right now and expensive to discover later.
/// <code>
/// off  size  field
///  0    4    payloadLength   uint32, see MaxPayloadLength
///  4    1    channel         IpcChannel
///  5    1    codec           PayloadCodec
///  6    2    messageKind     uint16, 0 on Data and Log
///  8    4    correlationId   uint32, 0 = notification
/// 12    4    reserved        must be 0
/// </code>
/// </remarks>
public readonly record struct FrameHeader
{
    /// <summary>Size of the encoded header in bytes.</summary>
    public const int Size = 16;

    /// <summary>
    /// Largest payload a single frame may carry, 16 MiB. A larger declared length fails the
    /// connection rather than the frame.
    /// </summary>
    /// <remarks>
    /// The length is the first thing read from an untrusted peer and it decides how much memory the
    /// reader is willing to buffer, so it is bounded before it is believed. Nothing legitimate comes
    /// close: the largest payload in the ecosystem is a few tens of KB.
    /// </remarks>
    public const int MaxPayloadLength = 16 * 1024 * 1024;

    /// <summary>
    /// Payload length in bytes, excluding this header.
    /// </summary>
    /// <remarks>
    /// Callers writing a frame leave this alone: <see cref="FrameWriter"/> stamps it from the payload
    /// it was actually handed. A header that disagrees with the bytes behind it desynchronises the
    /// stream permanently - every subsequent header would be read from the middle of a payload - so
    /// it is not a value worth letting anyone supply by hand.
    /// </remarks>
    public int PayloadLength { get; init; }

    /// <summary>Which stream this frame belongs to.</summary>
    public required IpcChannel Channel { get; init; }

    /// <summary>How the payload is encoded. Always <see cref="PayloadCodec.Json"/> on Control.</summary>
    public PayloadCodec Codec { get; init; }

    /// <summary>
    /// The control message type, so a frame can be routed or logged without being parsed. Zero on
    /// <see cref="IpcChannel.Data"/> and <see cref="IpcChannel.Log"/>.
    /// </summary>
    public ControlMessageKind MessageKind { get; init; }

    /// <summary>
    /// Ties a response to its request. Zero means a notification that expects no reply.
    /// </summary>
    public uint CorrelationId { get; init; }

    /// <summary>Writes this header into <paramref name="destination"/>, which must be at least <see cref="Size"/> bytes.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"A frame header needs {Size} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], (uint)PayloadLength);
        destination[4] = (byte)Channel;
        destination[5] = (byte)Codec;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], (ushort)MessageKind);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], CorrelationId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], 0u);
    }

    /// <summary>
    /// Parses a header from <paramref name="source"/> and validates every field.
    /// </summary>
    /// <exception cref="IpcProtocolException">
    /// A field is out of range. The connection is not recoverable at that point: the reader has no
    /// way to know where the next header begins, so the caller must tear the connection down rather
    /// than skip the frame.
    /// </exception>
    public static FrameHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"A frame header needs {Size} bytes.", nameof(source));

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        byte channel = source[4];
        byte codec = source[5];
        ushort messageKind = BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]);
        uint correlationId = BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]);

        if (payloadLength > MaxPayloadLength)
        {
            throw new IpcProtocolException(
                $"Frame declares a {payloadLength:N0} byte payload, over the {MaxPayloadLength:N0} byte limit.");
        }

        if (channel > (byte)IpcChannel.Log)
            throw new IpcProtocolException($"Frame declares unknown channel {channel}.");

        if (codec > (byte)PayloadCodec.MessagePack)
            throw new IpcProtocolException($"Frame declares unknown payload codec {codec}.");

        // Reserved is checked rather than ignored so that the day it becomes meaningful, an old peer
        // fails loudly instead of quietly discarding whatever a new peer put there.
        if (reserved != 0)
            throw new IpcProtocolException($"Frame reserved field is {reserved}, expected 0.");

        return new FrameHeader
        {
            PayloadLength = (int)payloadLength,
            Channel = (IpcChannel)channel,
            Codec = (PayloadCodec)codec,
            MessageKind = (ControlMessageKind)messageKind,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Parses a header from a sequence that may straddle several buffer segments.</summary>
    public static FrameHeader Read(in ReadOnlySequence<byte> source)
    {
        if (source.FirstSpan.Length >= Size)
            return Read(source.FirstSpan);

        Span<byte> contiguous = stackalloc byte[Size];
        source.Slice(0, Size).CopyTo(contiguous);
        return Read(contiguous);
    }
}
