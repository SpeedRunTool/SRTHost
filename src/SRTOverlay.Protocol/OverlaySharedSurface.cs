using System.Runtime.InteropServices;

namespace SRTOverlay.Protocol;

/// <summary>
/// The shared-surface handshake for step 3 of the overlay spike: the shim asks for a surface, a
/// producer creates it on the game's adapter and draws into it, and the shim composites it into the
/// game's frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is spike scaffolding, and it is drawn narrower than the shipped design on purpose.</b>
/// Section 11 has the real overlay plugin learn the surface parameters and publish its handles
/// <i>over the runner-to-shim pipe</i> - which is spike step 5. Step 3 proves only the graphics half:
/// that a texture written by a second process appears in the game's frame at all. With no pipe yet,
/// two tiny shared-memory control blocks stand in for those two pipe messages, and named shared
/// handles carry the textures and the fence. When the pipe arrives, the two control blocks become two
/// messages and this file's <see cref="RequestBlock"/>/<see cref="PublishBlock"/> collapse into the
/// pipe's message set. Nothing here is on the wire the shipped protocol version guarantees, so it does
/// not touch <see cref="OverlayProtocol.Version"/>.
/// </para>
/// <para>
/// Two blocks rather than one because each has a single writer, which is the only lock-free
/// arrangement that is obviously correct: the shim owns <see cref="RequestBlock"/> and writes it once
/// at initialisation; the producer owns <see cref="PublishBlock"/> and updates it every frame it
/// completes. Neither ever writes the other's block. The double-buffer handoff is the same one the
/// reference render thread uses: the producer publishes "the index of the newest complete texture and
/// the fence value that will be signalled when it is done", and the shim waits that value on the
/// game's own queue before sampling that index.
/// </para>
/// <para>
/// All names are prefixed <c>Local\</c> so they live in the caller's session namespace: the producer
/// and the game run as the same user, and a global name would need a privilege neither has a reason
/// to hold. The names are derived from a session id both sides are told out of band - the injector's
/// <see cref="OverlayStartupOptions.SessionId"/> for the shim, a matching <c>--session</c> for the
/// producer.
/// </para>
/// </remarks>
public static class OverlaySharedSurface
{
    /// <summary>Marks a control block as ours, so a stale or foreign mapping reads as not ready.</summary>
    public const uint Magic = 0x53525453; // "SRTS"

    /// <summary>Layout version of the two control blocks. Independent of the wire protocol version.</summary>
    public const uint SurfaceVersion = 1;

    /// <summary>The two textures the producer alternates between, so it never writes the one being read.</summary>
    public const int SurfaceCount = 2;

    /// <summary>Access mask for opening a shared handle: everything, since both sides are the same user.</summary>
    public const uint GenericAll = 0x10000000;

    /// <summary>Name of the shim-written request block, holding the surface parameters the producer must match.</summary>
    public static string RequestName(string session) => $@"Local\SRTOverlayRequest-{session}";

    /// <summary>Name of the producer-written publish block, holding the latest complete index and fence value.</summary>
    public static string PublishName(string session) => $@"Local\SRTOverlayPublish-{session}";

    /// <summary>Name of one of the shared textures, by index.</summary>
    public static string TextureName(string session, int index) => $@"Local\SRTOverlayTex-{session}-{index}";

    /// <summary>Name of the shared fence the producer signals and the shim waits.</summary>
    public static string FenceName(string session) => $@"Local\SRTOverlayFence-{session}";

    /// <summary>How far the shim has got in setting a surface up. Written by the shim, read by the producer.</summary>
    public enum RequestState : uint
    {
        /// <summary>The shim has not published a request yet. The default zero value of a fresh mapping.</summary>
        None = 0,

        /// <summary>
        /// The shim has hooked <c>Present</c>, knows the back buffer and the adapter, and is waiting for a
        /// producer to create the surface.
        /// </summary>
        Requested = 1,
    }

    /// <summary>
    /// What the shim publishes for the producer: the surface it wants, and the adapter it must be on.
    /// </summary>
    /// <remarks>
    /// Sequential and blittable on purpose - it is copied straight into a file mapping and read back
    /// on the other side with no serialiser. The adapter LUID is split into two 32-bit fields rather
    /// than carried as a struct so the layout is unambiguous across the boundary; adapter matching is
    /// mandatory, not an optimisation, because a shared handle will not open across adapters (section
    /// 11's hybrid-laptop note).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct RequestBlock
    {
        /// <summary><see cref="OverlaySharedSurface.Magic"/> once the block is valid.</summary>
        public uint Magic;

        /// <summary><see cref="SurfaceVersion"/>.</summary>
        public uint Version;

        /// <summary>The current <see cref="RequestState"/>.</summary>
        public uint State;

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
    }

    /// <summary>
    /// What the producer publishes back: which texture holds the newest frame, and when it is done.
    /// </summary>
    /// <remarks>
    /// The <see cref="FenceValue"/> is the value the producer's queue will signal on the shared fence
    /// once the render into <see cref="LatestIndex"/> has completed. The shim issues a queue-side
    /// <c>Wait</c> on that value before its own command list samples the texture, so the GPU orders the
    /// two processes' work without the CPU ever blocking on the render thread. <see cref="Alive"/> lets
    /// the shim stop compositing the instant the producer goes away rather than sampling a texture
    /// nothing is updating.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct PublishBlock
    {
        /// <summary><see cref="OverlaySharedSurface.Magic"/> once the block is valid.</summary>
        public uint Magic;

        /// <summary><see cref="SurfaceVersion"/>.</summary>
        public uint Version;

        /// <summary>Index of the texture holding the newest complete frame.</summary>
        public uint LatestIndex;

        /// <summary>Padding to keep <see cref="FenceValue"/> eight-byte aligned.</summary>
        public uint Reserved;

        /// <summary>Fence value signalled when <see cref="LatestIndex"/> is finished rendering.</summary>
        public ulong FenceValue;

        /// <summary>Non-zero while the producer is still running.</summary>
        public uint Alive;
    }
}
