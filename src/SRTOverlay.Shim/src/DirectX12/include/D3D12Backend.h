// The Direct3D 12 backend: hooks Present, ResizeBuffers and ExecuteCommandLists, and composites a
// texture another process rendered into the game's frame.
//
// Every detour obeys the render-thread rules: no allocation, no formatting and no logging on the
// steady-state paths, and each is wrapped so a fault ends in calling the original and returning rather
// than in a crash that takes the game down. Initialisation logs, because it runs once.
//
// There is exactly one backend per process - the shim is injected once - which is why the machinery
// and detour state live at file scope in the .cpp. The class is the IOverlayBackend seam Core drives.

#ifndef SRTOVERLAY_D3D12_BACKEND_H
#define SRTOVERLAY_D3D12_BACKEND_H

#include "IOverlayBackend.h"

namespace srtoverlay::directx12
{
    class D3D12Backend final : public core::IOverlayBackend
    {
    public:
        const char* Name() const override { return "Direct3D 12"; }
        bool Initialize() override;
        void Shutdown() override;
        std::string DescribeActivity() override;
    };
}

#endif // SRTOVERLAY_D3D12_BACKEND_H
