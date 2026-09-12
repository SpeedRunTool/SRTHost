using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SRTOverlay.Protocol;

/// <summary>
/// A message on the runner-to-shim overlay pipe, identified in the reused frame header's
/// <c>messageKind</c> field.
/// </summary>
/// <remarks>
/// The overlay pipe is a dedicated pipe between the shim (client) and the overlay plugin's runner
/// (server), and a protocol of its own: it borrows SRTHost.Ipc's 16-byte frame header for framing but
/// nothing else, so these kinds are independent of <c>SRTHost.Ipc.ControlMessageKind</c> and never
/// travel the router's pipe. Every value is on the wire and guarded by
/// <see cref="OverlayProtocol.Version"/>; this is the C# mirror of <c>SrtOverlayMessageKind</c> in the
/// C++ shim's <c>OverlayProtocol.h</c>.
/// </remarks>
public enum OverlayMessageKind : ushort
{
    /// <summary>Not an overlay message.</summary>
    None = 0,

    /// <summary>
    /// Shim → consumer: the surface the shim wants, and the adapter it must be created on. Sent once
    /// after the compositor initialises, and again after each <c>ResizeBuffers</c> renegotiation.
    /// </summary>
    SurfaceRequest = 1,

    /// <summary>
    /// Consumer → shim: which shared texture holds the newest complete frame, and the fence value that
    /// guards it. Sent every frame the consumer completes.
    /// </summary>
    SurfacePublish = 2,

    // Reserved for spike step 6 (input and lifetime), not yet on the wire:
    //   InputBatch = 16,   // shim → consumer: batched mouse/keyboard while interactive
    //   OverlayState = 17, // shim → consumer: visible/interactive state changes
}

/// <summary>
/// Shim → consumer. The surface the shim wants, and the adapter it must be created on.
/// </summary>
/// <remarks>
/// Sequential and packed to one byte, so it crosses the pipe as raw memory the C++ shim reads straight
/// into <c>SrtOverlaySurfaceRequest</c> with no serialiser - the field order and the total size must
/// match that struct. The adapter LUID is split into two 32-bit halves rather than carried as a struct
/// so the layout is unambiguous across the boundary; adapter matching is mandatory because a shared
/// handle will not open across adapters.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SurfaceRequest
{
    /// <summary>Encoded size in bytes; must equal <c>sizeof(SrtOverlaySurfaceRequest)</c>.</summary>
    public const int Size = 24;

    /// <summary>Back buffer width, in pixels.</summary>
    public uint Width;

    /// <summary>Back buffer height, in pixels.</summary>
    public uint Height;

    /// <summary>The <c>DXGI_FORMAT</c> the surface should use.</summary>
    public int Format;

    /// <summary>Low 32 bits of the game device's adapter LUID.</summary>
    public uint AdapterLuidLow;

    /// <summary>High 32 bits of the game device's adapter LUID.</summary>
    public int AdapterLuidHigh;

    /// <summary>Textures the consumer must create; equals <see cref="OverlaySharedSurface.SurfaceCount"/>.</summary>
    public uint SurfaceCount;

    /// <summary>The adapter LUID, rejoined from its two halves.</summary>
    public readonly long AdapterLuid => ((long)AdapterLuidHigh << 32) | AdapterLuidLow;

    /// <summary>Write this message's bytes into <paramref name="destination"/> (at least <see cref="Size"/> bytes).</summary>
    public readonly void WriteTo(Span<byte> destination) => OverlayPipeMessage.Write(this, destination);

    /// <summary>Read a message from <paramref name="source"/>, or <see langword="null"/> if it is too short.</summary>
    public static SurfaceRequest? TryRead(ReadOnlySpan<byte> source) => OverlayPipeMessage.Read<SurfaceRequest>(source, Size);
}

/// <summary>
/// Consumer → shim. Which texture holds the newest complete frame, and the fence value that guards it.
/// </summary>
/// <remarks>
/// The <see cref="FenceValue"/> is what the consumer's queue will signal on the shared fence once the
/// render into <see cref="LatestIndex"/> has completed; the shim issues a queue-side <c>Wait</c> on it
/// before its own command list samples the texture, so the GPU orders the two processes' work without
/// the render thread ever blocking. <see cref="Alive"/> lets the shim stop compositing the instant the
/// consumer pauses or goes away rather than sampling a texture nothing is updating.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SurfacePublish
{
    /// <summary>Encoded size in bytes; must equal <c>sizeof(SrtOverlaySurfacePublish)</c>.</summary>
    public const int Size = 16;

    /// <summary>Index of the texture holding the newest complete frame.</summary>
    public uint LatestIndex;

    /// <summary>Non-zero while the consumer is still producing.</summary>
    public uint Alive;

    /// <summary>Fence value signalled when <see cref="LatestIndex"/> is finished rendering.</summary>
    public ulong FenceValue;

    /// <summary>Write this message's bytes into <paramref name="destination"/> (at least <see cref="Size"/> bytes).</summary>
    public readonly void WriteTo(Span<byte> destination) => OverlayPipeMessage.Write(this, destination);

    /// <summary>Read a message from <paramref name="source"/>, or <see langword="null"/> if it is too short.</summary>
    public static SurfacePublish? TryRead(ReadOnlySpan<byte> source) => OverlayPipeMessage.Read<SurfacePublish>(source, Size);
}

/// <summary>Blittable read/write for the overlay pipe messages, matching the C++ shim's raw-struct reads.</summary>
/// <remarks>
/// The shim reads each message straight into a <c>#pragma pack(1)</c> struct, so the wire bytes are the
/// struct's native little-endian layout. Both ends are x86-family, so a blittable copy produces exactly
/// those bytes; the split-LUID and eight-byte-alignment choices in the structs keep that unambiguous.
/// </remarks>
internal static class OverlayPipeMessage
{
    internal static void Write<T>(in T message, Span<byte> destination) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (destination.Length < size)
            throw new ArgumentException($"An overlay pipe message needs {size} bytes.", nameof(destination));

        MemoryMarshal.Write(destination, in message);
    }

    internal static T? Read<T>(ReadOnlySpan<byte> source, int size) where T : unmanaged
        => source.Length < size ? null : MemoryMarshal.Read<T>(source);
}
