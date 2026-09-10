using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SRTOverlay.Core;

namespace SRTOverlay.DirectX12;

/// <summary>
/// The Direct3D 12 backend: hooks <c>Present</c>, <c>ResizeBuffers</c> and
/// <c>ExecuteCommandLists</c>, and - at this spike step - draws nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// Spike step 2 of section 11. The gate is that the detour is genuinely called on the game's render
/// thread, that the game keeps running, that it coexists with REFramework, and that the process
/// gained no writable+executable page. Drawing is step 3, and deliberately not here: a hook that
/// returns straight to the original is the smallest thing that can prove the mechanism, and it keeps
/// the first thing ever run inside a game as boring as possible.
/// </para>
/// <para>
/// <b>Every detour obeys the rules that make this survivable.</b> No allocation, no LINQ, no string
/// formatting and no logging on any of these paths - they run on the game's render thread, where a
/// gen-0 collection is a visible hitch. Nothing may throw across the boundary either: each detour is
/// wrapped so that the worst case is calling the original and returning, because an escaping
/// exception is a <c>FailFast</c> and a <c>FailFast</c> here takes the game with it.
/// </para>
/// <para>
/// Public only because <c>SRTOverlay.Native</c> - a different assembly - is what chooses the backend
/// and wires it in. Everything it depends on, the raw interop and the vtable resolver, stays internal:
/// the shim's public surface is this one type and the two exports.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Backend : IOverlayBackend
{
    // Static, because a detour is a plain function pointer with nowhere to put an instance. There is
    // exactly one backend per process, which is what makes that acceptable.
    private static nint originalPresent;
    private static nint originalResizeBuffers;
    private static nint originalExecuteCommandLists;

    private static long presentCount;
    private static long resizeBuffersCount;
    private static long executeCommandListsCount;
    private static nint lastCommandQueue;
    private static nint lastSwapChain;

    private VTableHook? presentHook;
    private VTableHook? resizeBuffersHook;
    private VTableHook? executeCommandListsHook;

    public string Name => "Direct3D 12";

    public bool Initialize()
    {
        try
        {
            D3D12VTableResolver.VTables vtables = D3D12VTableResolver.Resolve();
            if (!vtables.IsComplete)
            {
                OverlayLog.Write("D3D12: vtable resolution produced nothing; not hooking.");
                return false;
            }

            OverlayLog.Write("D3D12 vtables resolved:");
            OverlayLog.Write($"  swapchain vtable      0x{vtables.SwapChainVTable:X}");
            OverlayLog.Write($"  command queue vtable  0x{vtables.CommandQueueVTable:X}");
            OverlayLog.Write($"  Present               0x{vtables.Present:X}");
            OverlayLog.Write($"  ResizeBuffers         0x{vtables.ResizeBuffers:X}");
            OverlayLog.Write($"  ExecuteCommandLists   0x{vtables.ExecuteCommandLists:X}");

            originalPresent = vtables.Present;
            originalResizeBuffers = vtables.ResizeBuffers;
            originalExecuteCommandLists = vtables.ExecuteCommandLists;

            presentHook = VTableHook.Install(
                "Present", vtables.SwapChainVTable, Direct3D12.IDXGISwapChain_Present,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, int>)&PresentDetour);

            resizeBuffersHook = VTableHook.Install(
                "ResizeBuffers", vtables.SwapChainVTable, Direct3D12.IDXGISwapChain_ResizeBuffers,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int, uint, int>)&ResizeBuffersDetour);

            executeCommandListsHook = VTableHook.Install(
                "ExecuteCommandLists", vtables.CommandQueueVTable, Direct3D12.ID3D12CommandQueue_ExecuteCommandLists,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)&ExecuteCommandListsDetour);

            OverlayLog.Write(
                $"D3D12 hooks installed: Present={(presentHook is not null)}, " +
                $"ResizeBuffers={(resizeBuffersHook is not null)}, " +
                $"ExecuteCommandLists={(executeCommandListsHook is not null)}.");

            if (presentHook is null)
            {
                OverlayLog.Write(
                    "D3D12: the Present slot could not be written. Nothing is hooked; the game is untouched.");
                Shutdown();
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            OverlayLog.WriteException("D3D12 backend initialisation", exception);
            Shutdown();
            return false;
        }
    }

    public void Shutdown()
    {
        // Order matters only in that the hooks must all be gone before anything they reference is,
        // and there is nothing else to release at this step.
        Uninstall(ref executeCommandListsHook);
        Uninstall(ref resizeBuffersHook);
        Uninstall(ref presentHook);
    }

    private static void Uninstall(ref VTableHook? hook)
    {
        if (hook is null)
            return;

        bool removed = hook.Uninstall();
        OverlayLog.Write(removed
            ? $"D3D12: {hook.Name} hook removed."
            : $"D3D12: {hook.Name} hook could NOT be removed - something else replaced the slot after " +
              "us, and tearing that out would point it at a function it no longer expects.");

        hook = null;
    }

    public string DescribeActivity()
    {
        long presents = Interlocked.Read(ref presentCount);
        long resizes = Interlocked.Read(ref resizeBuffersCount);
        long executes = Interlocked.Read(ref executeCommandListsCount);

        return string.Create(CultureInfo.InvariantCulture,
            $"Present {presents:N0} (swapchain 0x{Volatile.Read(ref lastSwapChain):X}), " +
            $"ResizeBuffers {resizes:N0}, " +
            $"ExecuteCommandLists {executes:N0} (queue 0x{Volatile.Read(ref lastCommandQueue):X}), " +
            $"Present hook still installed: {presentHook?.IsInstalled}");
    }

    /// <summary>
    /// The game's <c>Present</c>. Counts the call, records the swapchain, and calls the original.
    /// </summary>
    /// <remarks>
    /// This is the function whose cost the whole phase is budgeted around - under 1 ms, measured on
    /// the game's own thread. At this step it is two interlocked writes and an indirect call, which
    /// is the floor; what step 3 adds on top of it is the thing that will need measuring.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PresentDetour(nint swapChain, uint syncInterval, uint flags)
    {
        Interlocked.Increment(ref presentCount);
        Volatile.Write(ref lastSwapChain, swapChain);

        var original = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)originalPresent;
        return original(swapChain, syncInterval, flags);
    }

    /// <summary>
    /// The game's <c>ResizeBuffers</c>, which is where every device resource would have to be
    /// rebuilt once there are any.
    /// </summary>
    /// <remarks>
    /// Hooked now, doing nothing, because the resolution and alt-tab paths are where an overlay
    /// usually breaks and it is worth knowing early how often the game actually calls it.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ResizeBuffersDetour(nint swapChain, uint bufferCount, uint width, uint height, int format, uint flags)
    {
        Interlocked.Increment(ref resizeBuffersCount);

        var original = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int, uint, int>)originalResizeBuffers;
        return original(swapChain, bufferCount, width, height, format, flags);
    }

    /// <summary>
    /// The game's <c>ExecuteCommandLists</c>, hooked to learn which command queue it presents with.
    /// </summary>
    /// <remarks>
    /// A D3D12 swapchain does not expose the queue it was created with, and step 3 needs one to
    /// submit the composite on. The reference tool records the last DIRECT queue it sees;
    /// <b>this does not yet filter by type</b>, because reading the queue description means another
    /// vtable call on the hot path and the filtering only matters once something is actually
    /// submitted. Recording the pointer now proves the queue vtable hook works, which is the part
    /// that could have failed.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ExecuteCommandListsDetour(nint commandQueue, uint numCommandLists, nint* commandLists)
    {
        Interlocked.Increment(ref executeCommandListsCount);
        Volatile.Write(ref lastCommandQueue, commandQueue);

        var original = (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)originalExecuteCommandLists;
        original(commandQueue, numCommandLists, commandLists);
    }
}
