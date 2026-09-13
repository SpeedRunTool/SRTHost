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
        Assert.Equal(16, (int)OverlayMessageKind.InputBatch);
        Assert.Equal(17, (int)OverlayMessageKind.OverlayState);
    }

    [Fact]
    public void InputMessageSizesMatchTheContract()
    {
        // The numbers the C++ static_asserts hold to (SrtOverlayInputEvent / InputBatchHeader / State).
        Assert.Equal(24, InputEvent.Size);
        Assert.Equal(24, Unsafe.SizeOf<InputEvent>());
        Assert.Equal(8, InputBatchHeader.Size);
        Assert.Equal(8, Unsafe.SizeOf<InputBatchHeader>());
        Assert.Equal(12, OverlayState.Size);
        Assert.Equal(12, Unsafe.SizeOf<OverlayState>());
    }

    [Fact]
    public void InputTypesAndFlagsAreStableWireValues()
    {
        // Mirror SrtOverlayInputType and SRT_OVERLAY_INPUT_FLAG_* in the C++ shim.
        Assert.Equal(1, (int)OverlayInputType.MouseMove);
        Assert.Equal(2, (int)OverlayInputType.MouseButton);
        Assert.Equal(3, (int)OverlayInputType.MouseWheel);
        Assert.Equal(4, (int)OverlayInputType.Key);
        Assert.Equal(5, (int)OverlayInputType.Char);
        Assert.Equal(6, (int)OverlayInputType.RawMouse);
        Assert.Equal(7, (int)OverlayInputType.RawKey);
        Assert.Equal(8, (int)OverlayInputType.Focus);

        Assert.Equal(0x0001, (int)OverlayInputFlags.Shift);
        Assert.Equal(0x0002, (int)OverlayInputFlags.Control);
        Assert.Equal(0x0004, (int)OverlayInputFlags.Alt);
        Assert.Equal(0x0008, (int)OverlayInputFlags.Repeat);
        Assert.Equal(0x0010, (int)OverlayInputFlags.Extended);
        Assert.Equal(0x0020, (int)OverlayInputFlags.Absolute);

        Assert.Equal(0, (int)OverlayMouseButton.Left);
        Assert.Equal(4, (int)OverlayMouseButton.X2);
    }

    [Fact]
    public void InputEventHasTheExactLittleEndianLayoutTheConsumerReads()
    {
        InputEvent keyDown = new()
        {
            Type = OverlayInputType.Key,        // 0x0004
            Flags = OverlayInputFlags.Shift | OverlayInputFlags.Extended, // 0x0011
            Code = 0x41,                        // 'A'
            X = 30,                             // scan code
            Y = 0,
            Value = 1,                          // down
            TimeMs = 0x11223344,
        };

        byte[] bytes = new byte[InputEvent.Size];
        keyDown.WriteTo(bytes);

        byte[] expected =
        [
            0x04, 0x00,             // Type
            0x11, 0x00,             // Flags
            0x41, 0x00, 0x00, 0x00, // Code
            0x1E, 0x00, 0x00, 0x00, // X
            0x00, 0x00, 0x00, 0x00, // Y
            0x01, 0x00, 0x00, 0x00, // Value
            0x44, 0x33, 0x22, 0x11, // TimeMs
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void OverlayStateHasTheExactLittleEndianLayoutTheConsumerReads()
    {
        OverlayState state = new()
        {
            Visible = 1,
            Interactive = 1,
            WindowFocused = 1,
            Reserved = 0,
            CursorX = 640,   // 0x00000280
            CursorY = -1,    // 0xFFFFFFFF
        };

        byte[] bytes = new byte[OverlayState.Size];
        state.WriteTo(bytes);

        byte[] expected =
        [
            0x01, 0x01, 0x01, 0x00, // Visible, Interactive, WindowFocused, Reserved
            0x80, 0x02, 0x00, 0x00, // CursorX
            0xFF, 0xFF, 0xFF, 0xFF, // CursorY
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void AnInputBatchTravelsAsAHeaderFollowedByItsEvents()
    {
        // The shim frames an InputBatch as [InputBatchHeader][count * InputEvent] in one Control frame.
        // This proves the consumer reads the header, then that many events, back out of that payload.
        InputEvent[] events =
        [
            new() { Type = OverlayInputType.MouseMove, X = 100, Y = 200 },
            new() { Type = OverlayInputType.MouseButton, Code = (uint)OverlayMouseButton.Left, Value = 1, X = 100, Y = 200 },
        ];
        InputBatchHeader batchHeader = new() { Count = (uint)events.Length, Dropped = 3 };

        byte[] payload = new byte[InputBatchHeader.Size + events.Length * InputEvent.Size];
        batchHeader.WriteTo(payload);
        for (int i = 0; i < events.Length; i++)
            events[i].WriteTo(payload.AsSpan(InputBatchHeader.Size + i * InputEvent.Size));

        InputBatchHeader readHeader = Assert.NotNull(InputBatchHeader.TryRead(payload));
        Assert.Equal(2u, readHeader.Count);
        Assert.Equal(3u, readHeader.Dropped);

        for (int i = 0; i < readHeader.Count; i++)
        {
            InputEvent read = Assert.NotNull(InputEvent.TryRead(payload.AsSpan(InputBatchHeader.Size + i * InputEvent.Size)));
            Assert.Equal(events[i].Type, read.Type);
            Assert.Equal(events[i].Code, read.Code);
            Assert.Equal(events[i].X, read.X);
            Assert.Equal(events[i].Y, read.Y);
            Assert.Equal(events[i].Value, read.Value);
        }
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
