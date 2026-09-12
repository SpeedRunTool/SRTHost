project(SRTOverlayDirectX12)

# The Direct3D 12 backend: resolves the game's vtables, hooks Present/ResizeBuffers/ExecuteCommandLists,
# and composites one shared-texture quad into the game's frame. Where the C# shim hand-rolled every COM
# vtable call (and where its two re9.exe crashes lived), this uses the real d3d12/dxgi headers and
# ComPtr, so the compiler gets every ABI and slot right - the whole reason the plan moved to C++.
add_library(SRTOverlay_DirectX12 STATIC
    D3D12VTableResolver.cpp
    D3D12Compositor.cpp
    D3D12Backend.cpp
)
add_library(SRTOverlay::DirectX12 ALIAS SRTOverlay_DirectX12)

target_compile_options(SRTOverlay_DirectX12 PRIVATE
    /W4 /WX /permissive- /Zc:__cplusplus /EHsc /sdl /GS /guard:cf
)

target_include_directories(SRTOverlay_DirectX12
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
)

# d3d12 and dxgi come with the Windows SDK. No d3dcompiler: the shim ships precompiled bytecode and
# never compiles a shader inside a game. No dxguid needed - the interface IIDs come through
# __uuidof on the SDK's declspec(uuid) interfaces.
target_link_libraries(SRTOverlay_DirectX12
    PUBLIC SRTOverlay_Core SRTOverlay_Protocol
    PRIVATE d3d12 dxgi
)
