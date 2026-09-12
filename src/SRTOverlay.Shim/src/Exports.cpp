// The shim's entire unmanaged surface: three exports, each called on a remote thread the injector
// creates. Names are pinned by SRTOverlay.def and must stay stable across releases.
//
// No exception may leave any of these. The reference tool takes the game down on a fault here; so
// would we, so each export is wrapped. They are declared as thread start routines (WINAPI, one LPVOID
// parameter, DWORD return) because CreateRemoteThread is what calls them.

#include <windows.h>

#include "D3D12Backend.h"
#include "OverlayLog.h"
#include "OverlayProtocol.h"
#include "OverlayRuntime.h"

using srtoverlay::core::OverlayLog;
namespace rt = srtoverlay::core::OverlayRuntime;

namespace
{
    // The one backend for this process. A function-local static so it outlives the Start call and is
    // constructed on first use; there is exactly one shim per process, so one backend is right. Direct3D
    // 11 and Vulkan join here once they exist, selected at run time from the game's renderer.
    srtoverlay::directx12::D3D12Backend& Backend()
    {
        static srtoverlay::directx12::D3D12Backend backend;
        return backend;
    }
}

extern "C"
{
    // Start the overlay. `startupBlob` points to a SrtOverlayStartupOptions the injector wrote into
    // this process before creating the thread. Read once, never retained: the injector frees the
    // remote allocation as soon as the thread has been waited on.
    DWORD WINAPI SrtOverlayStart(LPVOID startupBlob)
    {
        try
        {
            const auto* options = static_cast<const SrtOverlayStartupOptions*>(startupBlob);
            return static_cast<DWORD>(rt::Start(options, &Backend()));
        }
        catch (...)
        {
            // The log may not be open yet, in which case this goes nowhere - still better than letting
            // an exception cross the boundary, which would end the game.
            OverlayLog::Write("SrtOverlayStart threw.");
            return static_cast<DWORD>(SrtOverlayStartResult_Failed);
        }
    }

    // Detach: release hooks and resources, stop the shim's threads. The module stays mapped until the
    // injector calls FreeLibrary (a native DLL genuinely unloads, unlike the NativeAOT shim) or the
    // owner watchdog self-unloads.
    DWORD WINAPI SrtOverlayStop(LPVOID /*unused*/)
    {
        try
        {
            rt::Stop();
            return 0;
        }
        catch (...)
        {
            OverlayLog::Write("SrtOverlayStop threw.");
            return static_cast<DWORD>(SrtOverlayStartResult_Failed);
        }
    }

    // Do nothing at all, successfully. A native DLL has no runtime to bootstrap, so a memory scan
    // taken around this call is expected to show the shim adding nothing - the point the C# shim's
    // 1 MiB NativeAOT reservation made impossible to state cleanly.
    DWORD WINAPI SrtOverlayProbe(LPVOID /*unused*/)
    {
        return 0;
    }
}
