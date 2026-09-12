// The shim's own module handle, captured once in DllMain (DLL_PROCESS_ATTACH) and read wherever the
// shim needs to talk about itself: FreeLibraryAndExitThread on the self-unload path, and the "which
// copy of the binary is loaded" line at the top of the log.
//
// Captured in DllMain rather than resolved from a function address at use time because
// GetModuleHandleExW-from-address adds a reference unless asked not to, and getting that wrong on the
// unload path leaks a ref and defeats the very unload it exists to serve.

#ifndef SRTOVERLAY_SHIM_MODULE_H
#define SRTOVERLAY_SHIM_MODULE_H

#include <windows.h>

namespace srtoverlay::core
{
    void SetShimModule(HMODULE module) noexcept;
    HMODULE GetShimModule() noexcept;
}

#endif // SRTOVERLAY_SHIM_MODULE_H
