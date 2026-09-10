using System.Runtime.InteropServices;
using SRTOverlay.Core;
using SRTOverlay.Protocol;

namespace SRTOverlay.Native;

/// <summary>
/// The shim's entire unmanaged surface: two exports, both called on a remote thread.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>DllMain</c> here, and that is the design rather than an omission. A NativeAOT
/// shared library brings up a runtime and a garbage collector on its first managed call; doing that
/// under the loader lock is a deadlock waiting to happen, and worse than the ordinary case the
/// reference tool's <c>DllMain</c>-to-<c>ThreadMain</c> handoff already avoids. So the injector maps
/// the DLL with <c>LoadLibraryW</c>, whose entry point does nothing at all, and then makes a second
/// <c>CreateRemoteThread</c> onto <see cref="SrtOverlayStart"/> - which runs on an ordinary thread
/// with the loader lock released.
/// </para>
/// <para>
/// <b>No managed exception may leave either method.</b> An escaping exception across an
/// <c>[UnmanagedCallersOnly]</c> boundary is a <c>FailFast</c>, and a <c>FailFast</c> in this process
/// takes someone's game with it. Both catch <see cref="Exception"/> and return a value.
/// </para>
/// </remarks>
public static class Exports
{
    /// <summary>
    /// Start the overlay. The thread's exit code is an <see cref="OverlayStartResult"/>.
    /// </summary>
    /// <param name="startupBlob">
    /// Pointer to a UTF-16, null-terminated JSON <see cref="OverlayStartupOptions"/> in this
    /// process's memory, written there by the injector before the thread was created.
    /// </param>
    /// <remarks>
    /// The pointer is read once, immediately, and never retained: the injector frees the remote
    /// allocation as soon as the thread has been waited on, and holding it past that point would be
    /// a use-after-free inside a game.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = OverlayProtocol.StartExport)]
    public static int SrtOverlayStart(nint startupBlob)
    {
        try
        {
            string? json = startupBlob == 0 ? null : Marshal.PtrToStringUni(startupBlob);
            return (int)OverlayRuntime.Start(json);
        }
        catch (Exception exception)
        {
            // The log may not be open yet, in which case this goes nowhere - which is still better
            // than the alternative, because the alternative is FailFast.
            OverlayLog.WriteException(nameof(SrtOverlayStart), exception);
            return (int)OverlayStartResult.Failed;
        }
    }

    /// <summary>
    /// Do nothing at all, successfully.
    /// </summary>
    /// <remarks>
    /// The smallest possible managed call. Calling it is enough to bring up the NativeAOT runtime in
    /// the target and nothing else, so a memory scan taken around it attributes any allocation to the
    /// bootstrap rather than to <see cref="SrtOverlayStart"/>. That distinction is what section 11's
    /// claim about never allocating an executable page depends on being able to make.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = OverlayProtocol.ProbeExport)]
    public static int SrtOverlayProbe() => 0;

    /// <summary>
    /// Detach the overlay: release hooks and resources and stop. The DLL stays resident.
    /// </summary>
    /// <remarks>
    /// Named Stop rather than Unload throughout, because NativeAOT cannot remove its runtime from a
    /// live process and calling this "unload" would set an expectation the shim can never meet.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = OverlayProtocol.StopExport)]
    public static int SrtOverlayStop()
    {
        try
        {
            OverlayRuntime.Stop();
            return 0;
        }
        catch (Exception exception)
        {
            OverlayLog.WriteException(nameof(SrtOverlayStop), exception);
            return (int)OverlayStartResult.Failed;
        }
    }
}
