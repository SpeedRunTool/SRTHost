project(SRTOverlayCore)

# Backend-agnostic half of the shim: the injection entry point's real work, logging, the vtable hook,
# lifetime, the owner watchdog and the heartbeat. Nothing here knows what a swapchain is; a backend
# behind IOverlayBackend does. A static library linked whole into the shipped SRTOverlay.dll.
add_library(SRTOverlay_Core STATIC
    OverlayLog.cpp
    VTableHook.cpp
    OverlayRuntime.cpp
    ShimModule.cpp
)
add_library(SRTOverlay::Core ALIAS SRTOverlay_Core)

target_compile_options(SRTOverlay_Core PRIVATE
    /W4 /WX /permissive- /Zc:__cplusplus /EHsc /sdl /GS /guard:cf
)

target_include_directories(SRTOverlay_Core
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_link_libraries(SRTOverlay_Core PUBLIC SRTOverlay_Protocol)
