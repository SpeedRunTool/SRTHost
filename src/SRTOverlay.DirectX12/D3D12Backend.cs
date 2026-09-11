using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SRTOverlay.Core;

namespace SRTOverlay.DirectX12;

/// <summary>
/// The Direct3D 12 backend: hooks <c>Present</c>, <c>ResizeBuffers</c> and <c>ExecuteCommandLists</c>,
/// and composites a texture another process rendered into the game's frame.
/// </summary>
/// <remarks>
/// <para>
/// Spike steps 2 and 3 of section 11. Step 2 proved the hooks hold; step 3 is the draw: once the
/// steady frame count has passed and <c>ExecuteCommandLists</c> has revealed a DIRECT queue, a
/// <see cref="D3D12Compositor"/> is initialised on the game's device and queue, and each <c>Present</c>
/// composites one quad from a shared texture. With no producer running, the compositor stands ready
/// and draws nothing, so this is a superset of step 2's behaviour rather than a replacement for it.
/// </para>
/// <para>
/// <b>Every detour still obeys the render-thread rules.</b> No allocation, no LINQ, no formatting and
/// no logging on the <c>Present</c>, <c>ResizeBuffers</c> or <c>ExecuteCommandLists</c> paths in
/// steady state; each is wrapped so a fault ends in calling the original and returning rather than in
/// a <c>FailFast</c> that would take the game down. Initialisation logs, because it runs once.
/// </para>
/// <para>
/// The detours are static function pointers, so the state they touch is static too. There is exactly
/// one backend per process - the shim is injected once - which is what makes that sound.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class D3D12Backend : IOverlayBackend
{
    // The reference waits 5 x back-buffer-count Presents before initialising, so a DIRECT queue has
    // certainly been observed through ExecuteCommandLists. The back buffer count is not known until
    // the swapchain is in hand, so a fixed upper bound (5 x 4) is used; it costs a few frames of
    // delay before the overlay appears and nothing else.
    private const int FramesUntilInit = 20;

    private static nint originalPresent;
    private static nint originalResizeBuffers;
    private static nint originalExecuteCommandLists;

    private static long presentCount;
    private static long resizeBuffersCount;
    private static long executeCommandListsCount;
    private static nint lastCommandQueue;
    private static nint lastSwapChain;

    // Step 3 state. capturedDirectQueue is the game's rendering queue, learned from
    // ExecuteCommandLists and read by Present to initialise the compositor on.
    private static nint capturedDirectQueue;
    private static long framesSeen;

    // Queue pointers already probed for their type, so GetDesc runs a handful of times rather than on
    // every submit. Only a few distinct queues ever flow through ExecuteCommandLists, and this is only
    // touched before a DIRECT queue is captured. A best-effort cache: a race can re-probe a queue,
    // which is harmless now that the probe is correct and read-only.
    private const int MaxTestedQueues = 16;
    private static readonly nint[] testedQueues = new nint[MaxTestedQueues];
    private static int testedCount;
    private static D3D12Compositor? compositor;
    private static volatile bool compositorEnabled;
    private static volatile int compositorState; // 0 = not initialised, 1 = ready, 2 = failed
    private static volatile bool resizePending;

    private VTableHook? presentHook;
    private VTableHook? resizeBuffersHook;
    private VTableHook? executeCommandListsHook;

    public string Name => "Direct3D 12";

    public bool Initialize()
    {
        try
        {
            // A session id is what names the shared surface. Without one - as in the notepad control
            // run - the backend still installs its hooks and observes the game, but composites nothing.
            string? session = OverlayRuntime.Options?.SessionId;
            if (!string.IsNullOrEmpty(session))
            {
                compositor = new D3D12Compositor(session);
                compositorEnabled = true;
            }
            else
            {
                OverlayLog.Write("D3D12: no session id in the startup options; hooks only, no compositing.");
            }

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
        // Hooks first: the game may be calling through them right now, and everything else exists to
        // serve them. Once they are out, no detour can reach the compositor being torn down.
        Uninstall(ref executeCommandListsHook);
        Uninstall(ref resizeBuffersHook);
        Uninstall(ref presentHook);

        compositorEnabled = false;
        D3D12Compositor? toDispose = Interlocked.Exchange(ref compositor, null);
        toDispose?.Dispose();
        compositorState = 0;
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

        string composite = compositor is { } c ? c.DescribeActivity() : "no compositor";
        string state = compositorState switch { 1 => "ready", 2 => "failed", _ => "pending" };

        return string.Create(CultureInfo.InvariantCulture,
            $"Present {presents:N0} (swapchain 0x{Volatile.Read(ref lastSwapChain):X}), " +
            $"ResizeBuffers {resizes:N0}, " +
            $"ExecuteCommandLists {executes:N0} (queue 0x{Volatile.Read(ref lastCommandQueue):X}), " +
            $"compositor {state} [{composite}], " +
            $"Present hook still installed: {presentHook?.IsInstalled}");
    }

    /// <summary>
    /// The game's <c>Present</c>: advance the compositor's lifecycle, composite this frame, and always
    /// call the original.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PresentDetour(nint swapChain, uint syncInterval, uint flags)
    {
        Interlocked.Increment(ref presentCount);
        Volatile.Write(ref lastSwapChain, swapChain);

        try
        {
            DriveCompositor(swapChain, flags);
        }
        catch
        {
            // No managed exception may cross this boundary. A fault here disables compositing rather
            // than risking a repeat; the original is still called below so the game presents normally.
            compositorState = 2;
        }

        var original = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)originalPresent;
        return original(swapChain, syncInterval, flags);
    }

    private static void DriveCompositor(nint swapChain, uint flags)
    {
        if (!compositorEnabled || compositorState == 2)
            return;

        D3D12Compositor? c = compositor;
        if (c is null)
            return;

        // Rebuild after a resize before anything else touches the swapchain's buffers.
        if (resizePending)
        {
            if (c.Reinitialize(swapChain))
                resizePending = false;
            else
                compositorState = 2;
            return;
        }

        if (compositorState == 0)
        {
            // Wait until a DIRECT queue has certainly been seen, then initialise on it.
            if (Interlocked.Increment(ref framesSeen) <= FramesUntilInit)
                return;

            nint queue = Volatile.Read(ref capturedDirectQueue);
            if (queue == 0)
                return;

            compositorState = c.Initialize(swapChain, queue) ? 1 : 2;

            // Skip compositing on the very frame we initialised on, exactly as the reference does:
            // the swapchain is mid-Present and the freshly built resources have not been through a
            // full frame yet.
            return;
        }

        // A test present validates the swapchain without showing anything; do not draw on it.
        if ((flags & Direct3D12.DXGI_PRESENT_TEST) != 0)
            return;

        c.Composite(swapChain);
    }

    /// <summary>
    /// The game's <c>ResizeBuffers</c>: tear the compositor's back-buffer resources down before the
    /// game releases its buffers, and arm a rebuild on the next <c>Present</c>.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ResizeBuffersDetour(nint swapChain, uint bufferCount, uint width, uint height, int format, uint flags)
    {
        Interlocked.Increment(ref resizeBuffersCount);

        try
        {
            if (compositorEnabled && compositorState == 1 && compositor is { } c)
            {
                c.PrepareForResize();
                resizePending = true;
                compositorState = 0; // re-run the init path (which now takes the Reinitialize branch)
            }
        }
        catch
        {
            compositorState = 2;
        }

        var original = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int, uint, int>)originalResizeBuffers;
        return original(swapChain, bufferCount, width, height, format, flags);
    }

    /// <summary>
    /// The game's <c>ExecuteCommandLists</c>: learn the DIRECT queue the game presents with, then get
    /// out of the way.
    /// </summary>
    /// <remarks>
    /// Now filtered by queue type, which step 2 deliberately left out. A swapchain does not expose the
    /// queue it was created with and the reference records the last DIRECT one seen; the copy engine
    /// and compute queues also flow through here, so recording any of them would submit the composite
    /// on a queue that cannot present. The <c>GetDesc</c> call costs one vtable call and only runs
    /// until a DIRECT queue is captured, after which this is a single counter increment again.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ExecuteCommandListsDetour(nint commandQueue, uint numCommandLists, nint* commandLists)
    {
        Interlocked.Increment(ref executeCommandListsCount);
        Volatile.Write(ref lastCommandQueue, commandQueue);

        try
        {
            if (compositorEnabled && Volatile.Read(ref capturedDirectQueue) == 0 && ShouldProbe(commandQueue) &&
                Direct3D12.GetCommandQueueType(commandQueue) == Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT)
            {
                Interlocked.CompareExchange(ref capturedDirectQueue, commandQueue, 0);
            }
        }
        catch
        {
            // Reading the descriptor must never take the game down; worst case the queue is not
            // captured this call and is captured on the next.
        }

        var original = (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)originalExecuteCommandLists;
        original(commandQueue, numCommandLists, commandLists);
    }

    /// <summary>Whether this queue still needs a type probe, recording it if so.</summary>
    /// <remarks>
    /// Bounds the number of <c>GetDesc</c> calls to the handful of distinct queues a game submits on.
    /// Best-effort and lock-free: a race can probe the same queue twice, which is harmless.
    /// </remarks>
    private static bool ShouldProbe(nint queue)
    {
        int count = Volatile.Read(ref testedCount);
        for (int i = 0; i < count; i++)
        {
            if (Volatile.Read(ref testedQueues[i]) == queue)
                return false;
        }

        if (count >= MaxTestedQueues)
            return false; // give up probing rather than grow unbounded; a DIRECT queue was surely seen

        testedQueues[count] = queue;
        Volatile.Write(ref testedCount, count + 1);
        return true;
    }
}
