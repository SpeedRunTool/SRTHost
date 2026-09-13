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

    /// <summary>
    /// Shim → consumer: an <see cref="InputBatchHeader"/> followed by <c>count</c>
    /// <see cref="InputEvent"/> records. Only sent while interactive.
    /// </summary>
    InputBatch = 16,

    /// <summary>
    /// Shim → consumer: an <see cref="OverlayState"/>. Sent once per connection after the surface
    /// request, and again whenever the visible, interactive or focus state changes.
    /// </summary>
    OverlayState = 17,
}

/// <summary>
/// What one <see cref="InputEvent"/> describes, and so what its generic fields mean. Mirrors
/// <c>SrtOverlayInputType</c> in the C++ shim's <c>OverlayProtocol.h</c>.
/// </summary>
public enum OverlayInputType : ushort
{
    /// <summary>Not an input event.</summary>
    None = 0,

    /// <summary><c>X</c>, <c>Y</c>: pointer position in surface pixels.</summary>
    MouseMove = 1,

    /// <summary><c>Code</c>: <see cref="OverlayMouseButton"/>; <c>Value</c>: 1 down, 0 up; <c>X</c>, <c>Y</c>: position.</summary>
    MouseButton = 2,

    /// <summary><c>Code</c>: 0 vertical, 1 horizontal; <c>Value</c>: delta (120 per notch); <c>X</c>, <c>Y</c>: position.</summary>
    MouseWheel = 3,

    /// <summary><c>Code</c>: virtual-key code; <c>Value</c>: 1 down, 0 up; <c>X</c>: scan code; <c>Flags</c>: REPEAT, EXTENDED.</summary>
    Key = 4,

    /// <summary><c>Code</c>: one UTF-16 code unit, as <c>WM_CHAR</c> delivers it.</summary>
    Char = 5,

    /// <summary><c>X</c>, <c>Y</c>: motion (deltas unless ABSOLUTE); <c>Code</c>: raw button flags; <c>Value</c>: raw button data.</summary>
    RawMouse = 6,

    /// <summary><c>Code</c>: virtual-key code; <c>Value</c>: 1 down, 0 up; <c>X</c>: make code; <c>Flags</c>: EXTENDED.</summary>
    RawKey = 7,

    /// <summary><c>Value</c>: 1 the game window gained focus, 0 it lost it (release held keys).</summary>
    Focus = 8,
}

/// <summary>A mouse button, in an <see cref="InputEvent"/> of type <see cref="OverlayInputType.MouseButton"/>.</summary>
public enum OverlayMouseButton : uint
{
    Left = 0,
    Right = 1,
    Middle = 2,
    X1 = 3,
    X2 = 4,
}

/// <summary>Bit flags on an <see cref="InputEvent.Flags"/>, mirroring <c>SRT_OVERLAY_INPUT_FLAG_*</c>.</summary>
[Flags]
public enum OverlayInputFlags : ushort
{
    None = 0,

    /// <summary>Shift was down when the event was captured.</summary>
    Shift = 0x0001,

    /// <summary>Control was down when the event was captured.</summary>
    Control = 0x0002,

    /// <summary>Alt was down when the event was captured.</summary>
    Alt = 0x0004,

    /// <summary>A key auto-repeat, not a fresh press.</summary>
    Repeat = 0x0008,

    /// <summary>An extended key: right Ctrl/Alt, the arrows, numpad Enter.</summary>
    Extended = 0x0010,

    /// <summary><see cref="OverlayInputType.RawMouse"/> X/Y are absolute, not deltas.</summary>
    Absolute = 0x0020,
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

/// <summary>
/// Shim → consumer. One captured input event, forwarded only while interactive.
/// </summary>
/// <remarks>
/// Generic fields whose meaning depends on <see cref="Type"/> (see <see cref="OverlayInputType"/>) keep
/// every event one fixed size, so a batch is a plain array. Sequential and packed to one byte to match
/// the C++ shim's <c>SrtOverlayInputEvent</c>, which reads it straight off the pipe.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InputEvent
{
    /// <summary>Encoded size in bytes; must equal <c>sizeof(SrtOverlayInputEvent)</c>.</summary>
    public const int Size = 24;

    /// <summary>What this event describes.</summary>
    public OverlayInputType Type;

    /// <summary>Modifier and detail flags in force when the event was captured.</summary>
    public OverlayInputFlags Flags;

    /// <summary>Per <see cref="Type"/>: a virtual-key, mouse button, wheel axis, UTF-16 unit, or raw button flags.</summary>
    public uint Code;

    /// <summary>Per <see cref="Type"/>: surface-pixel X, a raw delta X, or a scan/make code.</summary>
    public int X;

    /// <summary>Per <see cref="Type"/>: surface-pixel Y, or a raw delta Y.</summary>
    public int Y;

    /// <summary>Per <see cref="Type"/>: 1 down / 0 up, a wheel delta, or a focus flag.</summary>
    public int Value;

    /// <summary><c>GetMessageTime()</c> of the window message that carried the event.</summary>
    public uint TimeMs;

    /// <summary>Write this event's bytes into <paramref name="destination"/> (at least <see cref="Size"/> bytes).</summary>
    public readonly void WriteTo(Span<byte> destination) => OverlayPipeMessage.Write(this, destination);

    /// <summary>Read an event from <paramref name="source"/>, or <see langword="null"/> if it is too short.</summary>
    public static InputEvent? TryRead(ReadOnlySpan<byte> source) => OverlayPipeMessage.Read<InputEvent>(source, Size);
}

/// <summary>
/// Shim → consumer. Leads every <see cref="OverlayMessageKind.InputBatch"/> payload; <see cref="Count"/>
/// <see cref="InputEvent"/> records follow it in the same frame.
/// </summary>
/// <remarks>
/// <see cref="Dropped"/> is non-zero when the shim's bounded capture queue overflowed since the previous
/// batch: the consumer should then treat all held keys and buttons as released and resync, because a key
/// it saw go down may never get its matching up. Packed to match <c>SrtOverlayInputBatchHeader</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InputBatchHeader
{
    /// <summary>Encoded size in bytes; must equal <c>sizeof(SrtOverlayInputBatchHeader)</c>.</summary>
    public const int Size = 8;

    /// <summary>Events that follow this header, at most <see cref="OverlaySharedSurface.MaxInputBatch"/>.</summary>
    public uint Count;

    /// <summary>Events lost to a full queue since the previous batch; non-zero means resync held keys.</summary>
    public uint Dropped;

    /// <summary>Write this header's bytes into <paramref name="destination"/> (at least <see cref="Size"/> bytes).</summary>
    public readonly void WriteTo(Span<byte> destination) => OverlayPipeMessage.Write(this, destination);

    /// <summary>Read a header from <paramref name="source"/>, or <see langword="null"/> if it is too short.</summary>
    public static InputBatchHeader? TryRead(ReadOnlySpan<byte> source) => OverlayPipeMessage.Read<InputBatchHeader>(source, Size);
}

/// <summary>
/// Shim → consumer. The shim-owned overlay state: the shim owns the visible/interactive modes and the
/// hotkeys that change them, and tells the consumer so it can draw (or not) and pump input (or not).
/// </summary>
/// <remarks>
/// Sent once per connection after the surface request, and again on every change to visibility,
/// interactivity or the game window's keyboard focus. The cursor position seats the consumer's software
/// cursor when interactive mode begins. Packed to match <c>SrtOverlayState</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct OverlayState
{
    /// <summary>Encoded size in bytes; must equal <c>sizeof(SrtOverlayState)</c>.</summary>
    public const int Size = 12;

    /// <summary>Non-zero while the quad is composited.</summary>
    public byte Visible;

    /// <summary>Non-zero while the game's input is swallowed and forwarded; implies <see cref="Visible"/>.</summary>
    public byte Interactive;

    /// <summary>Non-zero while the game window has keyboard focus.</summary>
    public byte WindowFocused;

    /// <summary>Reserved, always zero.</summary>
    public byte Reserved;

    /// <summary>The OS cursor's surface-pixel X when the state changed, to seat a software cursor.</summary>
    public int CursorX;

    /// <summary>The OS cursor's surface-pixel Y when the state changed.</summary>
    public int CursorY;

    /// <summary>Write this state's bytes into <paramref name="destination"/> (at least <see cref="Size"/> bytes).</summary>
    public readonly void WriteTo(Span<byte> destination) => OverlayPipeMessage.Write(this, destination);

    /// <summary>Read a state from <paramref name="source"/>, or <see langword="null"/> if it is too short.</summary>
    public static OverlayState? TryRead(ReadOnlySpan<byte> source) => OverlayPipeMessage.Read<OverlayState>(source, Size);
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
