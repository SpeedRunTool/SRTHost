using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SRTOverlay.Protocol;

/// <summary>
/// Everything the shim is told at startup, written into the target process by the injector and passed
/// to <see cref="OverlayProtocol.StartExport"/> as a pointer to a packed blob.
/// </summary>
/// <remarks>
/// <para>
/// A packed fixed-size struct rather than JSON, since 2026-09-11. The injected shim is C++ now, and the
/// startup blob is the one place the two languages have to agree byte for byte; a serializer on each
/// side is exactly the drift the shared layout removes. This type is the C# mirror of
/// <c>SrtOverlayStartupOptions</c> in the C++ shim's <c>OverlayProtocol.h</c> - the field order, the
/// buffer sizes and the total <see cref="BlobSize"/> must match it, or the wire breaks silently.
/// </para>
/// <para>
/// The layout is <c>#pragma pack(1)</c> on the C++ side, so there is no padding: the offsets below are
/// the running sum of the field sizes, and the strings are fixed-size UTF-16 buffers, null-terminated
/// within their bounds. No reflection and no source generator, which suits the NativeAOT reference shim
/// this still feeds and costs nothing here.
/// </para>
/// </remarks>
public sealed record OverlayStartupOptions
{
    // Fixed UTF-16 buffer lengths, in characters, matching SRT_OVERLAY_MAX_* in OverlayProtocol.h.
    private const int MaxProcessChars = 64;
    private const int MaxSessionChars = 128;
    private const int MaxPipeChars = 128;
    private const int MaxLogPathChars = 520;

    // Byte offsets into the packed blob. Each string field occupies its char count times two bytes.
    private const int OffsetProtocolVersion = 0;   // int32
    private const int OffsetOwnerProcessId = 4;     // int32
    private const int OffsetHasBrightness = 8;      // uint8
    private const int OffsetForcePq = 9;            // uint8
    // bytes 10, 11 are reserved padding to keep the float four-byte aligned, matching the C++ struct.
    private const int OffsetOverlayBrightness = 12; // float32
    private const int OffsetTargetProcess = 16;                                  // 64 wchar
    private const int OffsetSessionId = OffsetTargetProcess + MaxProcessChars * 2; // 144, 128 wchar
    private const int OffsetPipeName = OffsetSessionId + MaxSessionChars * 2;       // 400, 128 wchar
    private const int OffsetLogPath = OffsetPipeName + MaxPipeChars * 2;            // 656, 520 wchar

    /// <summary>Size of the packed blob, in bytes. Must equal <c>sizeof(SrtOverlayStartupOptions)</c>.</summary>
    public const int BlobSize = OffsetLogPath + MaxLogPathChars * 2; // 1696

    /// <summary>Must equal <see cref="OverlayProtocol.Version"/> or the shim refuses to start.</summary>
    public int ProtocolVersion { get; init; }

    /// <summary>
    /// Base name of the process the injector believes it is in, with extension - <c>re9.exe</c>.
    /// Checked case-insensitively against the running module before anything else happens.
    /// </summary>
    public required string TargetProcess { get; init; }

    /// <summary>
    /// Name of the named pipe back to the overlay plugin's runner, without the <c>\.\pipe\</c> prefix.
    /// Absent or empty means "do not connect", which is what the injection spike passes.
    /// </summary>
    public string? PipeName { get; init; }

    /// <summary>Full path of the log file the shim writes. Empty disables file logging.</summary>
    public string? LogPath { get; init; }

    /// <summary>Identifies this host session and names the shared surface. Empty means composite nothing.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Process id of the overlay plugin's runner - the thing that injected this shim. The shim waits on
    /// it and detaches when it exits. Zero means nobody owns it.
    /// </summary>
    public int OwnerProcessId { get; init; }

    /// <summary>
    /// Overrides the composite brightness the shim would pick for the back buffer's format. Null uses
    /// the per-format default. On the wire this is a "has value" flag plus a float, so a null and a
    /// non-positive value both read back as "use the default".
    /// </summary>
    public float? OverlayBrightness { get; init; }

    /// <summary>
    /// Forces PQ (ST 2084) encoding, for an HDR10 10-bit back buffer a format check cannot tell from a
    /// 10-bit SDR one. On the wire this is one byte, so a null and an explicit <c>false</c> both read
    /// back as null (both mean "leave the per-format default").
    /// </summary>
    public bool? OverlayForcePq { get; init; }

    /// <summary>Marshal into the packed blob the injector writes into the target and the shim reads.</summary>
    public byte[] ToBlob()
    {
        byte[] blob = new byte[BlobSize];
        Span<byte> span = blob;

        BinaryPrimitives.WriteInt32LittleEndian(span[OffsetProtocolVersion..], ProtocolVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[OffsetOwnerProcessId..], OwnerProcessId);

        bool hasBrightness = OverlayBrightness is > 0f;
        span[OffsetHasBrightness] = (byte)(hasBrightness ? 1 : 0);
        span[OffsetForcePq] = (byte)(OverlayForcePq == true ? 1 : 0);
        BinaryPrimitives.WriteSingleLittleEndian(span[OffsetOverlayBrightness..], hasBrightness ? OverlayBrightness!.Value : 0f);

        WriteWide(span.Slice(OffsetTargetProcess, MaxProcessChars * 2), TargetProcess);
        WriteWide(span.Slice(OffsetSessionId, MaxSessionChars * 2), SessionId);
        WriteWide(span.Slice(OffsetPipeName, MaxPipeChars * 2), PipeName);
        WriteWide(span.Slice(OffsetLogPath, MaxLogPathChars * 2), LogPath);

        return blob;
    }

    /// <summary>
    /// Parse a blob read out of remote memory. Returns <see langword="null"/> for a blob too small to be
    /// one, because the only caller is a boundary that may not let an exception out.
    /// </summary>
    public static OverlayStartupOptions? FromBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < BlobSize)
            return null;

        return new OverlayStartupOptions
        {
            ProtocolVersion = BinaryPrimitives.ReadInt32LittleEndian(blob[OffsetProtocolVersion..]),
            OwnerProcessId = BinaryPrimitives.ReadInt32LittleEndian(blob[OffsetOwnerProcessId..]),
            TargetProcess = ReadWide(blob.Slice(OffsetTargetProcess, MaxProcessChars * 2)) ?? string.Empty,
            SessionId = ReadWide(blob.Slice(OffsetSessionId, MaxSessionChars * 2)),
            PipeName = ReadWide(blob.Slice(OffsetPipeName, MaxPipeChars * 2)),
            LogPath = ReadWide(blob.Slice(OffsetLogPath, MaxLogPathChars * 2)),
            OverlayBrightness = blob[OffsetHasBrightness] != 0
                ? BinaryPrimitives.ReadSingleLittleEndian(blob[OffsetOverlayBrightness..])
                : null,
            OverlayForcePq = blob[OffsetForcePq] != 0 ? true : null,
        };
    }

    /// <summary>Encode a string as UTF-16 into a fixed byte region, null-terminated and truncated to fit.</summary>
    private static void WriteWide(Span<byte> destination, string? value)
    {
        destination.Clear();
        if (string.IsNullOrEmpty(value))
            return;

        // Leave room for the terminating null within the buffer; the region is already zero-filled.
        int maxChars = destination.Length / 2;
        int chars = Math.Min(value.Length, maxChars - 1);
        Encoding.Unicode.GetBytes(value.AsSpan(0, chars), destination);
    }

    /// <summary>Read a null-terminated UTF-16 string out of a fixed byte region, or null if empty.</summary>
    private static string? ReadWide(ReadOnlySpan<byte> source)
    {
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(source);
        int end = chars.IndexOf('\0');
        if (end < 0)
            end = chars.Length;
        return end == 0 ? null : new string(chars[..end]);
    }
}
