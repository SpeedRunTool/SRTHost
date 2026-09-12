using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SRTHost.Ipc;
using SRTOverlay.Protocol;
using SRTPluginBase.Abstractions;

namespace SRTOverlay.Tests;

/// <summary>
/// The runner-to-shim pipe message set (spike step 5). These structs cross the pipe as raw memory the
/// C++ shim reads straight into <c>SrtOverlaySurfaceRequest</c> / <c>SrtOverlaySurfacePublish</c>, so
/// their exact byte layout is a cross-language contract - the C++ side pins it with a
/// <c>static_assert</c> on <c>sizeof</c>, and these tests pin it here, down to the individual bytes, so
/// the two cannot drift apart silently.
/// </summary>
public sealed class OverlayPipeMessageTests
{
    [Fact]
    public void MessageSizesMatchTheContract()
    {
        // The numbers the C++ static_asserts hold to. If either changes, a field was added or resized on
        // one side; bump OverlayProtocol.Version and change both together.
        Assert.Equal(24, SurfaceRequest.Size);
        Assert.Equal(24, Unsafe.SizeOf<SurfaceRequest>());
        Assert.Equal(16, SurfacePublish.Size);
        Assert.Equal(16, Unsafe.SizeOf<SurfacePublish>());
    }

    [Fact]
    public void MessageKindsAreStableWireValues()
    {
        // On the wire in the frame header's messageKind field, mirrored by SrtOverlayMessageKind (C++).
        Assert.Equal(0, (int)OverlayMessageKind.None);
        Assert.Equal(1, (int)OverlayMessageKind.SurfaceRequest);
        Assert.Equal(2, (int)OverlayMessageKind.SurfacePublish);
    }

    [Fact]
    public void SurfaceRequestHasTheExactLittleEndianLayoutTheShimReads()
    {
        SurfaceRequest request = new()
        {
            Width = 2560,               // 0x00000A00
            Height = 1440,              // 0x000005A0
            Format = 28,                // DXGI_FORMAT_R8G8B8A8_UNORM
            AdapterLuidLow = 0x1234ABCD,
            AdapterLuidHigh = 0x7F,
            SurfaceCount = 2,
        };

        byte[] bytes = new byte[SurfaceRequest.Size];
        request.WriteTo(bytes);

        byte[] expected =
        [
            0x00, 0x0A, 0x00, 0x00, // Width
            0xA0, 0x05, 0x00, 0x00, // Height
            0x1C, 0x00, 0x00, 0x00, // Format
            0xCD, 0xAB, 0x34, 0x12, // AdapterLuidLow
            0x7F, 0x00, 0x00, 0x00, // AdapterLuidHigh
            0x02, 0x00, 0x00, 0x00, // SurfaceCount
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void SurfacePublishHasTheExactLittleEndianLayoutTheShimReads()
    {
        SurfacePublish publish = new()
        {
            LatestIndex = 1,
            Alive = 1,
            FenceValue = 0xDEADBEEF12345678,
        };

        byte[] bytes = new byte[SurfacePublish.Size];
        publish.WriteTo(bytes);

        byte[] expected =
        [
            0x01, 0x00, 0x00, 0x00,                         // LatestIndex
            0x01, 0x00, 0x00, 0x00,                         // Alive
            0x78, 0x56, 0x34, 0x12, 0xEF, 0xBE, 0xAD, 0xDE, // FenceValue
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void FenceValueIsEightByteAlignedInTheSurfacePublish()
    {
        // A UINT64 read and written across processes; a misaligned eight-byte field is a torn read
        // waiting to happen. Alive sits ahead of it precisely to pad it to eight.
        nint offset = Marshal.OffsetOf<SurfacePublish>(nameof(SurfacePublish.FenceValue));
        Assert.Equal(8, (int)offset);
    }

    [Theory]
    [InlineData(2560u, 1440u, 28, 0x1234ABCDu, 0x7F)]
    [InlineData(3840u, 2160u, 24, 0xFFFFFFFFu, unchecked((int)0xFFFFFFFF))]
    public void SurfaceRequestRoundTrips(uint width, uint height, int format, uint luidLow, int luidHigh)
    {
        SurfaceRequest written = new()
        {
            Width = width,
            Height = height,
            Format = format,
            AdapterLuidLow = luidLow,
            AdapterLuidHigh = luidHigh,
            SurfaceCount = 2,
        };

        byte[] bytes = new byte[SurfaceRequest.Size];
        written.WriteTo(bytes);
        SurfaceRequest read = Assert.NotNull(SurfaceRequest.TryRead(bytes));

        Assert.Equal(width, read.Width);
        Assert.Equal(height, read.Height);
        Assert.Equal(format, read.Format);
        Assert.Equal(luidLow, read.AdapterLuidLow);
        Assert.Equal(luidHigh, read.AdapterLuidHigh);
        Assert.Equal(2u, read.SurfaceCount);
        Assert.Equal(written.AdapterLuid, read.AdapterLuid);
    }

    [Fact]
    public void SurfacePublishRoundTrips()
    {
        SurfacePublish written = new() { LatestIndex = 1, Alive = 1, FenceValue = 0xDEADBEEF12345678 };

        byte[] bytes = new byte[SurfacePublish.Size];
        written.WriteTo(bytes);
        SurfacePublish read = Assert.NotNull(SurfacePublish.TryRead(bytes));

        Assert.Equal(1u, read.LatestIndex);
        Assert.Equal(1u, read.Alive);
        Assert.Equal(0xDEADBEEF12345678, read.FenceValue);
    }

    [Fact]
    public void TryReadRejectsAShortBuffer()
    {
        Assert.Null(SurfaceRequest.TryRead(new byte[SurfaceRequest.Size - 1]));
        Assert.Null(SurfacePublish.TryRead(new byte[SurfacePublish.Size - 1]));
    }

    [Fact]
    public void AdapterLuidSurvivesTheSplitAndRejoin()
    {
        // A high bit set in either half must survive the split into the two 32-bit fields, or a shared
        // handle opens on the wrong adapter - which fails silently rather than loudly.
        const long luid = unchecked((long)0xF00DBEEF_C0FFEE01);

        SurfaceRequest request = new()
        {
            AdapterLuidLow = (uint)(luid & 0xFFFFFFFF),
            AdapterLuidHigh = (int)(luid >> 32),
        };

        Assert.Equal(luid, request.AdapterLuid);
    }

    [Fact]
    public void APublishTravelsInAReusedFrameHeaderAndReadsBackWhole()
    {
        // The overlay pipe borrows SRTHost.Ipc's 16-byte frame header byte for byte. A publish goes out
        // as one Control/Blittable frame with the overlay kind in messageKind; this proves the framing
        // the C++ shim's OverlayPipeClient reads on the other end.
        SurfacePublish publish = new() { LatestIndex = 1, Alive = 1, FenceValue = 0xDEADBEEF12345678 };

        Span<byte> payload = stackalloc byte[SurfacePublish.Size];
        publish.WriteTo(payload);

        FrameHeader header = new()
        {
            Channel = IpcChannel.Control,
            Codec = PayloadCodec.Blittable,
            MessageKind = (ControlMessageKind)(ushort)OverlayMessageKind.SurfacePublish,
            PayloadLength = payload.Length,
        };

        byte[] frame = new byte[FrameHeader.Size + payload.Length];
        header.Write(frame);
        payload.CopyTo(frame.AsSpan(FrameHeader.Size));

        FrameHeader readBack = FrameHeader.Read(frame);
        Assert.Equal(IpcChannel.Control, readBack.Channel);
        Assert.Equal(PayloadCodec.Blittable, readBack.Codec);
        Assert.Equal(OverlayMessageKind.SurfacePublish, (OverlayMessageKind)(ushort)readBack.MessageKind);
        Assert.Equal(SurfacePublish.Size, readBack.PayloadLength);

        SurfacePublish decoded = Assert.NotNull(SurfacePublish.TryRead(frame.AsSpan(FrameHeader.Size, readBack.PayloadLength)));
        Assert.Equal(publish.LatestIndex, decoded.LatestIndex);
        Assert.Equal(publish.Alive, decoded.Alive);
        Assert.Equal(publish.FenceValue, decoded.FenceValue);
    }
}
