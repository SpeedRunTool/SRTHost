// A graphics backend the shim can drive: resolve the game's entry points, hook them, let go.
//
// This keeps OverlayRuntime backend-agnostic, which section 11 asks for explicitly: Direct3D 12,
// Direct3D 11 and Vulkan all live inside the one shipped shim and are chosen at run time, so the
// lifetime code cannot be written against any one of them. It also keeps the dependency direction
// sane - Core holds the injection entry point and the lifetime, a backend holds everything that knows
// what a swapchain is, and only the shipped SRTOverlay target links every backend and wires one in.

#ifndef SRTOVERLAY_IOVERLAY_BACKEND_H
#define SRTOVERLAY_IOVERLAY_BACKEND_H

#include <string>

namespace srtoverlay::core
{
    class IOverlayBackend
    {
    public:
        virtual ~IOverlayBackend() = default;

        // Name for the log, e.g. "Direct3D 12".
        virtual const char* Name() const = 0;

        // Resolve entry points and install hooks. Called once, on the overlay's own thread. Returns
        // false if this backend is not usable in this process. Must not throw: its caller is a step
        // from an unmanaged boundary where an escaping exception would take the game down.
        virtual bool Initialize() = 0;

        // Remove hooks and release resources. Safe to call when nothing was installed.
        virtual void Shutdown() = 0;

        // One line describing what the backend has seen since it started, for the heartbeat log - the
        // only way to observe a hook that draws nothing. Read from a background thread on a timer,
        // never from the render path.
        virtual std::string DescribeActivity() = 0;
    };
}

#endif // SRTOVERLAY_IOVERLAY_BACKEND_H
