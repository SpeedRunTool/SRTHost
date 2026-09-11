using System.Runtime.InteropServices;
using SRTOverlay.Protocol;

namespace SRTOverlay.Tests;

/// <summary>
/// The shared-surface handshake blocks and their names. These cross a process boundary as raw memory,
/// so their layout is a contract between the shim and the producer even though it is not on the wire.
/// </summary>
public sealed unsafe class SharedSurfaceTests
{
    [Fact]
    public void FenceValueIsEightByteAlignedInThePublishBlock()
    {
        // The fence value is a UINT64 read and written across processes; a misaligned eight-byte field
        // is a torn read waiting to happen. The Reserved field exists precisely to pad it to eight.
        nint offset = Marshal.OffsetOf<OverlaySharedSurface.PublishBlock>(nameof(OverlaySharedSurface.PublishBlock.FenceValue));
        Assert.Equal(0, (int)offset % 8);
    }

    [Fact]
    public void RequestBlockRoundTripsThroughRawMemory()
    {
        OverlaySharedSurface.RequestBlock written = new()
        {
            Magic = OverlaySharedSurface.Magic,
            Version = OverlaySharedSurface.SurfaceVersion,
            State = (uint)OverlaySharedSurface.RequestState.Requested,
            Width = 2560,
            Height = 1440,
            Format = 28, // DXGI_FORMAT_R8G8B8A8_UNORM
            AdapterLuidLow = 0x1234ABCD,
            AdapterLuidHigh = 0x7F,
        };

        byte* buffer = stackalloc byte[sizeof(OverlaySharedSurface.RequestBlock)];
        *(OverlaySharedSurface.RequestBlock*)buffer = written;
        OverlaySharedSurface.RequestBlock read = *(OverlaySharedSurface.RequestBlock*)buffer;

        Assert.Equal(written, read);
        Assert.Equal(2560u, read.Width);
        Assert.Equal(0x7F, read.AdapterLuidHigh);
    }

    [Fact]
    public void AdapterLuidSurvivesTheSplitAndRejoin()
    {
        // The shim splits a signed 64-bit LUID into a low uint and a high int; the producer rejoins
        // them. A high bit set in either half must survive the trip, or a shared handle opens on the
        // wrong adapter - which fails silently rather than loudly.
        const long luid = unchecked((long)0xF00DBEEF_C0FFEE01);

        uint low = (uint)(luid & 0xFFFFFFFF);
        int high = (int)(luid >> 32);
        long rejoined = ((long)high << 32) | low;

        Assert.Equal(luid, rejoined);
    }

    [Theory]
    [InlineData("s3")]
    [InlineData("spike-42")]
    public void NamesAreSessionScopedAndDistinct(string session)
    {
        string request = OverlaySharedSurface.RequestName(session);
        string publish = OverlaySharedSurface.PublishName(session);
        string fence = OverlaySharedSurface.FenceName(session);
        string tex0 = OverlaySharedSurface.TextureName(session, 0);
        string tex1 = OverlaySharedSurface.TextureName(session, 1);

        // Session-local namespace, so no privilege beyond the caller's own session is needed.
        Assert.All([request, publish, fence, tex0, tex1], name => Assert.StartsWith(@"Local\", name));

        // Every name is distinct, or two things would collide on the same mapping/handle.
        string[] all = [request, publish, fence, tex0, tex1];
        Assert.Equal(all.Length, all.Distinct().Count());

        // The session scopes them, so two sessions never share a surface.
        Assert.Contains(session, request);
    }
}
