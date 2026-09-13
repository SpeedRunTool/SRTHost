project(SRTOverlayCore)

# Backend-agnostic half of the shim: the injection entry point's real work, logging, the vtable hook,
# lifetime, the owner watchdog and the heartbeat. Nothing here knows what a swapchain is; a backend
# behind IOverlayBackend does. A static library linked whole into the shipped SRTOverlay.dll.
add_library(SRTOverlay_Core STATIC
    OverlayLog.cpp
    VTableHook.cpp
    OverlayRuntime.cpp
    OverlayPipeClient.cpp
    OverlayInput.cpp
    CrashHandler.cpp
    ShimModule.cpp
)
add_library(SRTOverlay::Core ALIAS SRTOverlay_Core)

target_compile_options(SRTOverlay_Core PRIVATE
    /W4 /WX /permissive- /Zc:__cplusplus /EHsc /sdl /GS /guard:cf
)

target_include_directories(SRTOverlay_Core
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
)

# dbghelp for MiniDumpWriteDump, psapi for EnumProcessModules - both used by the crash handler.
# dxguid supplies IID_IDirectInput8W / GUID_SysKeyboard for the DirectInput8 input hook; DirectInput8Create
# itself is resolved at run time from dinput8.dll, so nothing links dinput8 (a game without it still loads
# the shim, which then relies on raw input alone). All Windows SDK libraries - no vcpkg dependency.
target_link_libraries(SRTOverlay_Core PUBLIC SRTOverlay_Protocol PRIVATE dbghelp psapi dxguid)
