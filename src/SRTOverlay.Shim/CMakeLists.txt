cmake_minimum_required(VERSION 3.31)
project(SRTOverlayShim LANGUAGES CXX)

# C++23, as decided in the plan's section 11. The RE9 reference is the model for this file's shape;
# ours is leaner because the shim composites one quad and draws no ImGui, so there are no third-party
# graphics dependencies to fetch - only the Windows SDK's d3d12/dxgi.
set(CMAKE_CXX_STANDARD 23) # 98, 11, 14, 17, 20, 23, 26
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}")
set(CMAKE_SHARED_LIBRARY_PREFIX "")
set(CMAKE_SHARED_LIBRARY_SUFFIX ".dll")
set(CMAKE_STATIC_LIBRARY_PREFIX "")
set(CMAKE_STATIC_LIBRARY_SUFFIX ".lib")

# One binary per architecture, both backends inside, and the two file names section 11 fixes as part
# of the shim's reputation: reputation attaches to a name that persists. The C# NativeAOT projects
# this supersedes emit exactly these names.
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set(SRT_OVERLAY_ARCH_BITS 64)
    set(SRT_OVERLAY_OUTPUT_NAME "SRTOverlay64")
else()
    set(SRT_OVERLAY_ARCH_BITS 32)
    set(SRT_OVERLAY_OUTPUT_NAME "SRTOverlay32")
endif()
message(STATUS "SRT overlay shim: ${SRT_OVERLAY_OUTPUT_NAME} (${SRT_OVERLAY_ARCH_BITS}-bit)")

# Release optimizations, lifted from the RE9 reference. /OPT:NOICF on the shipped RelWithDebInfo so
# identical functions are not folded onto one address - a crash inside a game arrives as a stack and
# folded frames name the wrong function.
add_compile_options(
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:/O2>"
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:/Ob2>"
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:/Oi>"
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:/Ot>"
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:/GL>"
)
add_link_options(
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:/LTCG>"
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:/INCREMENTAL:NO>"
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:/OPT:REF>"
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>>:/OPT:ICF>"
    "$<$<CONFIG:RELWITHDEBINFO>:/OPT:NOICF>"
)
add_compile_definitions(
    "$<$<OR:$<CONFIG:RELEASE>,$<CONFIG:MINSIZEREL>,$<CONFIG:RELWITHDEBINFO>>:RELEASE>"
    "$<$<OR:$<CONFIG:DEBUG>,$<CONFIG:RELWITHDEBINFO>>:DEBUG>"
    "UNICODE"
    "_UNICODE"
    "WIN32_LEAN_AND_MEAN"
    "NOMINMAX"
)

# Architecture macros the Windows headers want. The RE9 reference hardcodes the x64 pair; we select on
# the actual pointer size so the same tree builds SRTOverlay32.dll as well.
if(SRT_OVERLAY_ARCH_BITS EQUAL 64)
    add_compile_definitions("_AMD64_" "_WIN64")
else()
    add_compile_definitions("_X86_")
endif()

function(set_from_env var_name default_value var_type var_desc)
    if(DEFINED ENV{${var_name}})
        set(${var_name} $ENV{${var_name}} CACHE ${var_type} ${var_desc} FORCE)
    else()
        set(${var_name} ${default_value} CACHE ${var_type} ${var_desc} FORCE)
    endif()
endfunction()

string(ASCII 169 COPYRIGHT)

# VERSIONINFO properties. Same env-driven shape the RE9 AutomatedRelease.yml already feeds
# (RC_VERSION_* set from version.txt + the build number), so the shim slots into that pipeline.
set(RC_PRODUCTNAME "SRT Overlay Shim")
set(RC_FILEDESCRIPTION "The injected SRT overlay shim (Direct3D 12 compositor).")
set(RC_COMPANYNAME "SpeedRunTool")
set(RC_LEGALCOPYRIGHT "Copyright ${COPYRIGHT} 2026 SpeedRunTool")
set(RC_INTERNALNAME "${SRT_OVERLAY_OUTPUT_NAME}")
set(RC_ORIGINALFILENAME "${SRT_OVERLAY_OUTPUT_NAME}.dll")

# Major.Minor.Patch[-PreRel]+Build.Hash - the host and contracts sit at 5.0.0.
set_from_env(RC_VERSION_MAJOR 5 STRING "Version (major) of the product.")
set_from_env(RC_VERSION_MINOR 0 STRING "Version (minor) of the product.")
set_from_env(RC_VERSION_PATCH 0 STRING "Version (patch) of the product.")
set_from_env(RC_VERSION_PRERELEASE_TAG "" STRING "Pre-release tag of the product.")
set_from_env(RC_VERSION_BUILD_HASH "" STRING "Short SHA hash of the build.")
set_from_env(RC_VERSION_BUILD 0 STRING "Build number, auto-incremented in CI.")

set(RC_VERSION_SEMVER "${RC_VERSION_MAJOR}.${RC_VERSION_MINOR}.${RC_VERSION_PATCH}")
if(DEFINED RC_VERSION_PRERELEASE_TAG AND NOT RC_VERSION_PRERELEASE_TAG STREQUAL "")
    string(APPEND RC_VERSION_SEMVER "-${RC_VERSION_PRERELEASE_TAG}")
endif()
string(APPEND RC_VERSION_SEMVER "+${RC_VERSION_BUILD}")
if(DEFINED RC_VERSION_BUILD_HASH AND NOT RC_VERSION_BUILD_HASH STREQUAL "")
    string(APPEND RC_VERSION_SEMVER ".${RC_VERSION_BUILD_HASH}")
endif()

# The shared contract, mirrored by the C# injector/plugin. Header-only, so an INTERFACE library.
add_library(SRTOverlay_Protocol INTERFACE)
target_include_directories(SRTOverlay_Protocol INTERFACE "${CMAKE_CURRENT_SOURCE_DIR}/src/Protocol/include")

add_subdirectory(src/Core)
add_subdirectory(src/DirectX12)

# The one injected artefact. DllMain and the exports live directly in the shared target so the linker
# cannot drop them from a static library nothing internally references; the .def pins the exported
# names. Core and DirectX12 are static libraries linked whole into it.
configure_file(${CMAKE_CURRENT_SOURCE_DIR}/src/VersionInfo.rc.in ${PROJECT_BINARY_DIR}/VersionInfo.rc @ONLY)

add_library(SRTOverlay SHARED
    src/DllMain.cpp
    src/Exports.cpp
    src/SRTOverlay.def
    ${PROJECT_BINARY_DIR}/VersionInfo.rc
)

target_compile_options(SRTOverlay PRIVATE
    /W4             # warning level 4
    /WX             # warnings are errors
    /permissive-    # conformance mode
    /Zc:__cplusplus # correct __cplusplus
    /EHsc           # C++ exception model
    /sdl            # additional security checks
    /GS             # buffer security checks
    /guard:cf       # Control Flow Guard
)

target_link_libraries(SRTOverlay PRIVATE SRTOverlay_Core SRTOverlay_DirectX12)

set_target_properties(SRTOverlay PROPERTIES
    OUTPUT_NAME "${SRT_OVERLAY_OUTPUT_NAME}"
)
