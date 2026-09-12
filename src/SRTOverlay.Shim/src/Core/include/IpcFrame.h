// The 16-byte SRTHost.Ipc frame header, mirrored for the C++ shim.
//
// The overlay pipe (shim <-> the plugin's runner) reuses SRTHost.Ipc's framing byte for byte rather
// than inventing a second wire format - see the decision in the plan's section 11. This is the C++
// mirror of SRTHost.Ipc/FrameHeader.cs; that file is the authority, and the layout below must match it
// field for field:
//
//   off  size  field
//    0    4    payloadLength   uint32, bytes after this header
//    4    1    channel         IpcChannel
//    5    1    codec           IpcCodec
//    6    2    messageKind     uint16 (an SrtOverlayMessageKind on the overlay pipe)
//    8    4    correlationId   uint32 (0 = notification; the overlay pipe never correlates)
//   12    4    reserved        must be 0
//
// Every field is little-endian, spelled out explicitly rather than left to a struct memcpy: both ends
// are x86-family today, but the C# side states its endianness through BinaryPrimitives and this stays
// honest alongside it.

#ifndef SRTOVERLAY_IPC_FRAME_H
#define SRTOVERLAY_IPC_FRAME_H

#include <cstdint>

namespace srtoverlay::core
{
    // Mirrors SRTHost.Ipc.IpcChannel. The overlay pipe only ever uses Control.
    enum class IpcChannel : uint8_t
    {
        Control = 0,
        Data = 1,
        Log = 2,
    };

    // Mirrors SRTPluginBase.Abstractions.PayloadCodec. The overlay pipe carries packed structs, so
    // Blittable.
    enum class IpcCodec : uint8_t
    {
        Json = 0,
        Utf8Raw = 1,
        Blittable = 2,
        MessagePack = 3,
    };

    struct IpcFrameHeader
    {
        static constexpr int kSize = 16;

        // Largest payload a single frame may carry, matching SRTHost.Ipc.FrameHeader.MaxPayloadLength.
        // The overlay messages are tens of bytes; this only bounds what a malformed length can make the
        // reader believe before it tears the connection down.
        static constexpr uint32_t kMaxPayloadLength = 16u * 1024u * 1024u;

        uint32_t payloadLength = 0;
        IpcChannel channel = IpcChannel::Control;
        IpcCodec codec = IpcCodec::Blittable;
        uint16_t messageKind = 0;
        uint32_t correlationId = 0;

        // Write this header into dst[0..kSize). Little-endian, reserved zeroed.
        void Write(uint8_t* dst) const
        {
            WriteU32(dst + 0, payloadLength);
            dst[4] = static_cast<uint8_t>(channel);
            dst[5] = static_cast<uint8_t>(codec);
            WriteU16(dst + 6, messageKind);
            WriteU32(dst + 8, correlationId);
            WriteU32(dst + 12, 0u);
        }

        // Parse a header from src[0..kSize) and validate every field. Returns false rather than
        // throwing: the caller is a read loop on a foreign pipe and a malformed header means tear the
        // connection down, not crash. On false the connection is not recoverable - the reader has no
        // way to know where the next header begins.
        static bool TryRead(const uint8_t* src, IpcFrameHeader& out)
        {
            const uint32_t payloadLength = ReadU32(src + 0);
            const uint8_t channel = src[4];
            const uint8_t codec = src[5];
            const uint16_t messageKind = ReadU16(src + 6);
            const uint32_t correlationId = ReadU32(src + 8);
            const uint32_t reserved = ReadU32(src + 12);

            if (payloadLength > kMaxPayloadLength)
                return false;
            if (channel > static_cast<uint8_t>(IpcChannel::Log))
                return false;
            if (codec > static_cast<uint8_t>(IpcCodec::MessagePack))
                return false;
            if (reserved != 0)
                return false;

            out.payloadLength = payloadLength;
            out.channel = static_cast<IpcChannel>(channel);
            out.codec = static_cast<IpcCodec>(codec);
            out.messageKind = messageKind;
            out.correlationId = correlationId;
            return true;
        }

    private:
        static void WriteU16(uint8_t* p, uint16_t v)
        {
            p[0] = static_cast<uint8_t>(v & 0xFF);
            p[1] = static_cast<uint8_t>((v >> 8) & 0xFF);
        }

        static void WriteU32(uint8_t* p, uint32_t v)
        {
            p[0] = static_cast<uint8_t>(v & 0xFF);
            p[1] = static_cast<uint8_t>((v >> 8) & 0xFF);
            p[2] = static_cast<uint8_t>((v >> 16) & 0xFF);
            p[3] = static_cast<uint8_t>((v >> 24) & 0xFF);
        }

        static uint16_t ReadU16(const uint8_t* p)
        {
            return static_cast<uint16_t>(static_cast<uint16_t>(p[0]) | (static_cast<uint16_t>(p[1]) << 8));
        }

        static uint32_t ReadU32(const uint8_t* p)
        {
            return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) |
                   (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
        }
    };
}

#endif // SRTOVERLAY_IPC_FRAME_H
