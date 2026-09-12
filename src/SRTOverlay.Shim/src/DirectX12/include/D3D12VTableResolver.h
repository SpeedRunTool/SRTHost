// Finds the Direct3D 12 vtables the shim hooks, by creating throwaway COM objects and reading them.
//
// No scanning of the game and no waiting for the game to hand anything over: a vtable is a property of
// the implementation rather than of any object, so a swapchain we create ourselves and throw away
// exposes the same table the game's swapchain uses. The addresses survive releasing every object
// because they live in dxgi.dll and d3d12.dll data pages, mapped for the life of the process.
//
// The composition entry point is deliberate. REFramework hooks CreateSwapChainForHwnd; creating our
// throwaway swapchain through CreateSwapChainForComposition means the two tools do not trip over each
// other. Step 2 confirmed live that the game's swapchain uses the same vtable a composition swapchain
// does, which is what makes a vtable hook viable at all. The hwnd path is kept only as a fallback.

#ifndef SRTOVERLAY_D3D12_VTABLE_RESOLVER_H
#define SRTOVERLAY_D3D12_VTABLE_RESOLVER_H

namespace srtoverlay::directx12
{
    struct ResolvedVTables
    {
        void** swapChainVTable = nullptr;   // IDXGISwapChain3's vtable array
        void** commandQueueVTable = nullptr; // ID3D12CommandQueue's vtable array

        bool IsComplete() const { return swapChainVTable != nullptr && commandQueueVTable != nullptr; }
    };

    // Create throwaway objects, read their vtables, release everything. Returns an incomplete result
    // (IsComplete() == false) if the process is not Direct3D 12 or a step failed; the caller then does
    // not hook.
    ResolvedVTables ResolveVTables();
}

#endif // SRTOVERLAY_D3D12_VTABLE_RESOLVER_H
