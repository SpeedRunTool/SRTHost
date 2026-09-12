#include "ShimModule.h"

namespace srtoverlay::core
{
    namespace
    {
        HMODULE g_shimModule = nullptr;
    }

    void SetShimModule(HMODULE module) noexcept
    {
        g_shimModule = module;
    }

    HMODULE GetShimModule() noexcept
    {
        return g_shimModule;
    }
}
