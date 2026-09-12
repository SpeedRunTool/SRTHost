// DllMain does almost nothing, and that is the design rather than an omission.
//
// The injector maps the shim with LoadLibraryW - whose entry point runs under the loader lock - and
// then makes a SECOND remote thread onto the start export, which runs the real work with the lock
// released. The reference tool (SRTPluginRE9) uses the same DllMain->ThreadMain shape for the same
// reason: nothing that could take a lock, touch another module, or block may happen here.
//
// All this does is remember the module handle (for the self-unload path and the log's identity line)
// and turn off the per-thread DLL_THREAD_ATTACH/DETACH notifications the shim has no use for.

#include <windows.h>

#include "ShimModule.h"

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID /*reserved*/)
{
    switch (reason)
    {
        case DLL_PROCESS_ATTACH:
            srtoverlay::core::SetShimModule(module);
            DisableThreadLibraryCalls(module);
            break;
        default:
            break;
    }

    return TRUE;
}
