# SRTOverlay.Shim — the injected overlay shim, in C++23

This is the C++ port of the injected overlay shim, decided on 2026-09-11 (plan section 11, *Language:
C++ shim*), superseding the C# NativeAOT projects `SRTOverlay.Native` / `.Core` / `.DirectX12`. It
builds the one signed binary per architecture — `SRTOverlay64.dll` / `SRTOverlay32.dll` — that every
overlay plugin injects: it hooks the game's `Present`, waits a shared fence, and composites one quad
from a texture another process rendered. It draws **no ImGui** — the plugin draws in its own process —
so its dependency closure is deliberately near-empty.

## Why C++ (the short version)

1. **Unloadability — the driver.** A native DLL can `FreeLibraryAndExitThread` after detaching hooks
   and stopping its threads; NativeAOT could only ever *detach*, never unload. The owner watchdog here
   self-unloads, and `SrtOverlayStop` tears down so the injector can `FreeLibrary` and reinject a
   rebuilt shim without restarting the game.
2. **It deletes the hand-rolled-COM-ABI bug class.** Both live `re9.exe` crashes on the C# shim were
   hand-rolled interop (sret argument order, an off-by-one vtable slot). Over `<d3d12.h>` and `ComPtr`
   the compiler gets every ABI and slot right, so `Direct3D12.Composite.cs` and the `OverlaySelfCheck`
   that existed only to catch that bug class both disappear.
3. **No NativeAOT bootstrap RWX.** A native DLL has no embedded runtime, so it adds no executable page
   — the "allocates no executable page" reputation goal, cleanly rather than in the narrower measured
   form.

Only the injected binary changes language. The injector (`SRTOverlay.Injection`), the host, the
runners, `SRTPluginBase`, the plugin-side renderer and the throwaway `--produce-surface` producer stay
C#.

## Layout

```
CMakeLists.txt            top target: DllMain + Exports + .def -> SRTOverlay{64,32}.dll
CMakePresets.json         x64/x86 x Debug/Release presets, generator VS 18 2026, toolset v145
vcpkg.json                empty deps (no ImGui); the toolchain stays wired for future backends
src/Protocol/include/     the shared C contract, mirrored by the C# injector/plugin
  OverlayProtocol.h         exports, versions, SrtOverlayStartupOptions (packed), shared-surface blocks
  OverlaySharedNames.h      Local\ names for the request/publish blocks and shared handles
src/Core/                 backend-agnostic: OverlayLog, VTableHook, OverlayRuntime, ShimModule
src/DirectX12/            the D3D12 backend: vtable resolver, the Present hook, the compositor
  include/CompositeShaders.h  the precompiled DXBC, carried over verbatim from the C# shim
src/DllMain.cpp           captures the module handle; does nothing else under the loader lock
src/Exports.cpp           SrtOverlayStart / Stop / Probe
src/SRTOverlay.def        pins the three exported names (undecorated on x86 too)
```

`SRTOverlay.Core` and `SRTOverlay.DirectX12` are static libraries; the shipped `SRTOverlay` shared
target links both. Direct3D 11 and Vulkan become sibling backend libraries behind `IOverlayBackend`,
selected at run time — the seam the C# design already had, now first-class.

## Build

Needs the Windows SDK and MSVC v145 (VS 18 2026); `VCPKG_ROOT` set. No packages are fetched.

```powershell
cd S:\SpeedRunTool\SRTHost\src\SRTOverlay.Shim
cmake --preset "Release/Win/x64/MSVC"
cmake --build --preset "Release/Win/x64/MSVC"   # -> bin\Release\Win\x64\MSVC\RelWithDebInfo\SRTOverlay64.dll
cmake --workflow --preset "Release/Win/x86/MSVC" # x86 in one step -> SRTOverlay32.dll
```

Builds clean under `/W4 /WX`. The tree is deliberately **not** in `src/SRTHost.slnx` — it is a CMake
project, exactly as the RE9 reference keeps its C++ tree out of its C# solution. `RC_VERSION_*`
environment variables feed the version resource the same way the RE9 `AutomatedRelease.yml` feeds it.

## The startup contract changed: JSON → packed struct

The C# shim read a UTF-16 **JSON** blob. This shim reads a **packed `SrtOverlayStartupOptions`
struct** (`OverlayProtocol.h`) — one authoritative layout both languages agree on, no serializer to
drift, and no JSON dependency inside the injected binary. `SRT_OVERLAY_PROTOCOL_VERSION` guards it.

## The runner-to-shim pipe (spike step 5)

The surface handshake no longer uses shared memory. The overlay plugin's runner creates a named pipe
server and injects the shim with the pipe's name (`pipeName` in the startup blob); the shim connects as
the client (`OverlayPipeClient` in Core). Two messages cross it, both in `OverlayProtocol.h`: the shim
sends a `SrtOverlaySurfaceRequest` (size, format, adapter LUID), and the consumer replies with a
`SrtOverlaySurfacePublish` (newest texture index, fence value, alive) every completed frame. The
textures and the fence themselves still cross by name — only the negotiation moved onto the pipe.

The framing is SRTHost.Ipc's 16-byte frame header reused byte for byte (`IpcFrame.h` mirrors
`SRTHost.Ipc/FrameHeader.cs`), each message a Control/Blittable frame with the overlay kind in the
header's `messageKind`. All pipe I/O is on `OverlayPipeClient`'s own worker thread; the render thread
only ever calls `TryGetLatestPublish`, a bounded-spin seqlock read that never blocks — the compositor
must not stall the game's `Present` on a hung consumer.

## Not done here — the explicit next steps

1. ~~Switch `SRTOverlay.Injection` to write the packed struct~~ — **done** (protocol v1→v2 blob).
2. ~~Point the spike/injector at this DLL and re-run the RE2/RE8/RE9 gate~~ — **done, human-confirmed.**
3. **Step 5 shim side is built and unit-tested; the live gate is pending.** Both `SRTOverlay{64,32}.dll`
   build clean, the C# contract round-trips in `SRTOverlay.Tests`, and the spike's `--produce-surface
   --pipe <name>` drives the surface over the pipe. Still to run in a live game: inject the C++ shim
   with a `--pipe` name, run the pipe producer on the same name, and confirm the wash composites and
   detaches cleanly on RE9/RE8/RE2 — exactly the step-3 gate, now negotiated over the pipe.
4. **Steps 6–7 in C++**: input capture/forwarding and lifetime, then signing/refusal against the real
   Trusted Signing profile.
5. **CI**: a parallel `build-cpp` job in `main`'s `AutomatedRelease.yml` on `windows-*-vs2026`
   (bootstrap vcpkg → `cmake --preset` → `cmake --build`), merged into `package` and signed with the
   same `AS-TJGutjahr` / `AS-CP-TJGutjahr` Trusted Signing profile.
6. **Remove the superseded C# `SRTOverlay.Native` / `.Core` / `.DirectX12`** once this reaches parity.
   They remain the reference spec until then (the shared-memory transport in `SharedSurfaceProducer` /
   `SharedMemoryTransport` still drives the C# shim; the pipe transport lives in the spike).

`CompositeShaders.h` is generated from the C# shim's `Shaders/CompositeShaders.g.cs` (itself from
`Shaders/Composite.hlsl` via `Compile-Shaders.ps1`). When the HLSL changes, rerun that PowerShell
script and regenerate this header from the result.
