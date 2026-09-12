// What the shim does once it is inside the game, on an ordinary thread with the loader lock released.
//
// The injector calls LoadLibraryW to map the DLL, whose DllMain does almost nothing, and then makes a
// second remote thread onto the exported start function - which lands here. This is the managed-half
// role the C# OverlayRuntime played, now native; nothing in it may assume it runs under the loader
// lock, and nothing runs before it.

#ifndef SRTOVERLAY_RUNTIME_H
#define SRTOVERLAY_RUNTIME_H

#include "IOverlayBackend.h"
#include "OverlayProtocol.h"

namespace srtoverlay::core
{
    namespace OverlayRuntime
    {
        // Start the overlay. `backend` is the graphics backend to drive (owned by the caller; it must
        // outlive the shim, which the shipped exports guarantee by using a function-local static).
        // Returns rather than reports every failure through an out-parameter, because its caller is an
        // exported boundary whose only channel back to the injector is the thread's exit code.
        SrtOverlayStartResult Start(const SrtOverlayStartupOptions* options, IOverlayBackend* backend);

        // Detach: remove hooks, release resources, stop the shim's threads. After this returns the
        // module can be unloaded by the injector (FreeLibrary) - unlike the NativeAOT shim, a native
        // DLL genuinely unloads. Safe to call more than once.
        void Stop();

        // The options this shim started with, for a backend to read (session id, HDR overrides). Null
        // before Start or after Stop.
        const SrtOverlayStartupOptions* Options();
    }
}

#endif // SRTOVERLAY_RUNTIME_H
