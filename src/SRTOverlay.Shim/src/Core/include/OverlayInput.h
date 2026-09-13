// The overlay's modal input capture, forwarding and hotkeys - spike step 6.
//
// The shim owns two independent states, each bound to a hotkey (section 11, "Input"):
//   * VISIBLE     - the quad is composited. The default for a HUD. Gated in the backend's Present.
//   * INTERACTIVE - implies visible, and the shim swallows ALL mouse and keyboard input from the game
//                   and forwards it to the consumer instead. This is how the Steam overlay behaves.
//
// There is deliberately no per-widget hit-testing and therefore no frame-lag race: while interactive,
// every input event goes to the consumer and none to the game; while not, nothing is captured. The
// consumer feeds the forwarded events into its own ImGui context and draws its own cursor, which
// sidesteps fighting the game over ShowCursor and cursor clipping.
//
// Two capture points, exactly as the RE9 reference:
//   * the game window's WndProc (SetWindowLongPtr), for legacy mouse/keyboard messages and WM_INPUT;
//   * DirectInput8 GetDeviceState / GetDeviceData (vtable-hooked), because a game reading input through
//     DInput would otherwise keep moving the camera under an open panel even with the window messages
//     swallowed. Raw input (WM_INPUT) is handled in the WndProc for the same reason.
//
// This lives in Core, not a backend: it knows nothing about a swapchain. The backend hands it the game
// window once it has resolved it from the swapchain (AttachWindow); everything else - the modal state,
// the hotkeys, the DInput hooks, forwarding over the pipe - is backend-agnostic. Nothing here runs on
// the render thread: the WndProc and DInput detours run on the game's own input/UI threads, and they
// only enqueue onto the pipe client, which does the I/O on its worker.

#ifndef SRTOVERLAY_INPUT_H
#define SRTOVERLAY_INPUT_H

#include <windows.h>

#include <cstdint>
#include <string>

namespace srtoverlay::core
{
    class OverlayPipeClient;

    namespace OverlayInput
    {
        // Begin: record the hotkeys (0 for either takes the SRT_OVERLAY_DEFAULT_* code) and the pipe to
        // forward over, reset the state to visible-but-not-interactive, and hook DirectInput8 if the
        // game uses it. The window is not known yet; the backend calls AttachWindow once it has it.
        // `pipe` may be null (no consumer), in which case state and input are simply not forwarded.
        void Start(uint8_t visibleHotkey, uint8_t interactiveHotkey, OverlayPipeClient* pipe);

        // Hook the game window's WndProc. Called by the backend once it resolves the window from the
        // swapchain (DXGI_SWAP_CHAIN_DESC::OutputWindow). Idempotent: the same window is hooked once,
        // and a different window (a game that recreated its swapchain on another hwnd) is re-hooked.
        void AttachWindow(HWND window);

        // Whether the overlay should be composited this frame. The backend gates its Present on this.
        bool IsVisible();

        // Whether input is being swallowed from the game and forwarded to the consumer.
        bool IsInteractive();

        // Remove the WndProc and DirectInput hooks and clear state. Must run before the shim unloads,
        // for the same reason the backend's hooks must: a detour left pointing into an unmapped module
        // crashes the game. Safe to call when Start was never called or already undone.
        void Stop();

        // One line for the heartbeat log. Off the render path.
        std::string DescribeActivity();
    }
}

#endif // SRTOVERLAY_INPUT_H
