// The COM vtable slot indices the shim hooks. Properties of the COM interfaces, not of any language,
// so they are identical to the C# shim's Direct3D12.cs constants and to the C++ reference. Spelled out
// with the inheritance chain that produces each, because an off-by-one calls the wrong function
// through a pointer and crashes a game rather than failing a build.

#ifndef SRTOVERLAY_D3D12_SLOTS_H
#define SRTOVERLAY_D3D12_SLOTS_H

namespace srtoverlay::directx12
{
    // IDXGISwapChain: IUnknown(0-2), IDXGIObject(3-6), IDXGIDeviceSubObject GetDevice(7),
    // then Present(8), GetBuffer(9), SetFullscreenState(10), GetFullscreenState(11), GetDesc(12),
    // ResizeBuffers(13).
    inline constexpr int kSlot_IDXGISwapChain_Present = 8;
    inline constexpr int kSlot_IDXGISwapChain_ResizeBuffers = 13;

    // ID3D12CommandQueue: IUnknown(0-2), ID3D12Object(3-6), ID3D12DeviceChild GetDevice(7),
    // UpdateTileMappings(8), CopyTileMappings(9), ExecuteCommandLists(10).
    inline constexpr int kSlot_ID3D12CommandQueue_ExecuteCommandLists = 10;
}

#endif // SRTOVERLAY_D3D12_SLOTS_H
