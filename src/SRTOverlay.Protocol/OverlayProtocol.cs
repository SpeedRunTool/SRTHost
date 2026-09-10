namespace SRTOverlay.Protocol;

/// <summary>
/// Constants shared by the injected shim and whatever injects it.
/// </summary>
/// <remarks>
/// The shim is a single signed binary shared by every overlay plugin, so its exported names and its
/// startup contract are part of its reputation: they are expected to be stable across releases, and
/// changing one is a deliberate act rather than a refactor. See section 11 of the 5.0 plan.
/// </remarks>
public static class OverlayProtocol
{
    /// <summary>
    /// Version of the startup blob and of the runner-to-shim message set.
    /// </summary>
    /// <remarks>
    /// The shim refuses a blob whose <see cref="OverlayStartupOptions.ProtocolVersion"/> it does not
    /// recognise. An overlay plugin and the shim it injects ship in the same install, so a mismatch
    /// means something was substituted and refusing is the right answer.
    /// </remarks>
    public const int Version = 1;

    /// <summary>
    /// The shim's start export, called on a remote thread once <c>LoadLibraryW</c> has returned.
    /// </summary>
    /// <remarks>
    /// Takes a pointer to a UTF-16, null-terminated JSON <see cref="OverlayStartupOptions"/> written
    /// into the target by the injector. Returns an <see cref="OverlayStartResult"/> as its thread
    /// exit code.
    /// </remarks>
    public const string StartExport = "SrtOverlayStart";

    /// <summary>
    /// The shim's detach export: releases hooks and resources, leaving the DLL resident.
    /// </summary>
    /// <remarks>
    /// NativeAOT cannot unload its runtime from a live process, so this is genuinely a detach and
    /// not a <c>FreeLibrary</c>. Anything user-facing must say so.
    /// </remarks>
    public const string StopExport = "SrtOverlayStop";

    /// <summary>
    /// A do-nothing export, used to separate the cost of starting a managed runtime in the target
    /// from the cost of anything the shim's own startup does.
    /// </summary>
    /// <remarks>
    /// It exists because "the injection allocated an executable page" is not an answer - the runtime
    /// bootstrap and the shim's startup are two different suspects reached by one call, and calling
    /// this one first separates them. Cheap to keep, and the only way to re-ask the question on a
    /// future runtime without rebuilding a special binary.
    /// </remarks>
    public const string ProbeExport = "SrtOverlayProbe";

    /// <summary>Base file name of the 64-bit shim, without extension.</summary>
    public const string Shim64 = "SRTOverlay64";

    /// <summary>Base file name of the 32-bit shim, without extension.</summary>
    public const string Shim32 = "SRTOverlay32";

    /// <summary>The shim file name for the architecture of the process asking.</summary>
    public static string ShimFileName => Environment.Is64BitProcess ? Shim64 + ".dll" : Shim32 + ".dll";
}

/// <summary>
/// What <see cref="OverlayProtocol.StartExport"/> returns as its remote thread exit code.
/// </summary>
/// <remarks>
/// Deliberately a small set of numbers rather than an HRESULT: the injector reads it through
/// <c>GetExitCodeThread</c>, which is all it will ever get back from inside the game, and a value it
/// can name in a log line is worth more than one it has to look up.
/// </remarks>
public enum OverlayStartResult
{
    /// <summary>Started; the overlay thread owns the process from here.</summary>
    Success = 0,

    /// <summary>The startup blob was absent, truncated, or not valid JSON.</summary>
    BadArgument = 1,

    /// <summary>The blob names a protocol version this shim does not implement.</summary>
    ProtocolMismatch = 2,

    /// <summary>
    /// This process is not the one the blob named.
    /// </summary>
    /// <remarks>
    /// A mis-aimed injection stops itself rather than drawing over whatever it landed in. Cheap, and
    /// it is the honest answer to "what if the injector picks the wrong PID".
    /// </remarks>
    WrongProcess = 3,

    /// <summary>Already started in this process; the second call did nothing.</summary>
    AlreadyRunning = 4,

    /// <summary>Something threw where nothing is allowed to. The log has it.</summary>
    Failed = 5,
}
