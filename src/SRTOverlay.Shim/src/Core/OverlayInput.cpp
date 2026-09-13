#include "OverlayInput.h"

#define DIRECTINPUT_VERSION 0x0800

#include <windows.h>
#include <windowsx.h> // GET_X_LPARAM / GET_Y_LPARAM / GET_XBUTTON_WPARAM

#include <dinput.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "OverlayLog.h"
#include "OverlayPipeClient.h"
#include "OverlayProtocol.h"
#include "VTableHook.h"

namespace srtoverlay::core
{
    namespace
    {
        // ---- Modal state, owned here and read by the backend's Present gate ----
        std::atomic<bool> g_active{false};
        std::atomic<bool> g_visible{true};       // a HUD is visible by default (section 11)
        std::atomic<bool> g_interactive{false};  // input is passed to the game until the hotkey flips this
        std::atomic<bool> g_windowFocused{true};

        uint8_t g_visibleHotkey = SRT_OVERLAY_DEFAULT_VISIBLE_HOTKEY;
        uint8_t g_interactiveHotkey = SRT_OVERLAY_DEFAULT_INTERACTIVE_HOTKEY;

        OverlayPipeClient* g_pipe = nullptr;

        // ---- The game window and our WndProc hook ----
        std::mutex g_windowMutex; // AttachWindow may race with a resize on another thread
        HWND g_window = nullptr;
        WNDPROC g_originalWndProc = nullptr;

        std::atomic<long> g_eventsForwarded{0};
        std::atomic<long> g_hotkeyPresses{0};

        // ---- DirectInput8 hooks (shared vtable, so hooking a throwaway device hooks the game's too) ----
        using PfnGetDeviceState = HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8W*, DWORD, LPVOID);
        using PfnGetDeviceData =
            HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8W*, DWORD, LPDIDEVICEOBJECTDATA, LPDWORD, DWORD);

        VTableHook g_getDeviceStateHook;
        VTableHook g_getDeviceDataHook;
        PfnGetDeviceState g_originalGetDeviceState = nullptr;
        PfnGetDeviceData g_originalGetDeviceData = nullptr;

        constexpr int kSlot_GetDeviceState = 9;
        constexpr int kSlot_GetDeviceData = 10;

        // ---- Helpers ----

        uint16_t CurrentModifiers()
        {
            uint16_t flags = 0;
            if (GetKeyState(VK_SHIFT) & 0x8000)
                flags |= SRT_OVERLAY_INPUT_FLAG_SHIFT;
            if (GetKeyState(VK_CONTROL) & 0x8000)
                flags |= SRT_OVERLAY_INPUT_FLAG_CONTROL;
            if (GetKeyState(VK_MENU) & 0x8000)
                flags |= SRT_OVERLAY_INPUT_FLAG_ALT;
            return flags;
        }

        void Forward(const SrtOverlayInputEvent& event)
        {
            if (g_pipe != nullptr)
                g_pipe->EnqueueInput(event);
            g_eventsForwarded.fetch_add(1);
        }

        SrtOverlayInputEvent MakeEvent(SrtOverlayInputType type)
        {
            SrtOverlayInputEvent event{};
            event.type = static_cast<uint16_t>(type);
            event.flags = CurrentModifiers();
            event.timeMs = static_cast<uint32_t>(GetMessageTime());
            return event;
        }

        void PublishState()
        {
            if (g_pipe == nullptr)
                return;

            SrtOverlayState state{};
            state.visible = g_visible.load() ? 1 : 0;
            state.interactive = g_interactive.load() ? 1 : 0;
            state.windowFocused = g_windowFocused.load() ? 1 : 0;

            POINT cursor{};
            if (GetCursorPos(&cursor))
            {
                HWND window;
                {
                    std::lock_guard<std::mutex> lock(g_windowMutex);
                    window = g_window;
                }
                if (window != nullptr)
                    ScreenToClient(window, &cursor); // client pixels are surface pixels for a game window
                state.cursorX = cursor.x;
                state.cursorY = cursor.y;
            }

            g_pipe->SetOverlayState(state);
        }

        // Toggle a modal state if the key is one of our hotkeys. Returns true if the key was ours (so the
        // WndProc swallows it), whether or not this was the toggling (keydown) edge.
        bool HandleHotkey(WPARAM vk, bool keyDown, bool repeat)
        {
            if (vk == g_visibleHotkey)
            {
                if (keyDown && !repeat)
                {
                    const bool now = !g_visible.load();
                    g_visible.store(now);
                    // If we just hid the overlay, interactive makes no sense; drop it too.
                    if (!now)
                        g_interactive.store(false);
                    g_hotkeyPresses.fetch_add(1);
                    OverlayLog::Write(std::string("Input: visible -> ") + (now ? "on" : "off") + ".");
                    PublishState();
                }
                return true;
            }

            if (vk == g_interactiveHotkey)
            {
                if (keyDown && !repeat)
                {
                    const bool now = !g_interactive.load();
                    g_interactive.store(now);
                    if (now)
                        g_visible.store(true); // interactive implies visible
                    g_hotkeyPresses.fetch_add(1);
                    OverlayLog::Write(std::string("Input: interactive -> ") + (now ? "on (swallowing game input)" : "off") + ".");
                    PublishState();
                }
                return true;
            }

            return false;
        }

        // Translate one captured window message into forwarded events. Called only while interactive.
        void CaptureMessage(UINT msg, WPARAM wParam, LPARAM lParam)
        {
            switch (msg)
            {
                case WM_MOUSEMOVE:
                {
                    SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_MouseMove);
                    event.x = GET_X_LPARAM(lParam);
                    event.y = GET_Y_LPARAM(lParam);
                    Forward(event);
                    break;
                }

                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                {
                    SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_MouseButton);
                    event.x = GET_X_LPARAM(lParam);
                    event.y = GET_Y_LPARAM(lParam);

                    const bool down = (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN ||
                                       msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN);
                    event.value = down ? 1 : 0;

                    switch (msg)
                    {
                        case WM_LBUTTONDOWN:
                        case WM_LBUTTONUP:
                            event.code = SrtOverlayMouseButton_Left;
                            break;
                        case WM_RBUTTONDOWN:
                        case WM_RBUTTONUP:
                            event.code = SrtOverlayMouseButton_Right;
                            break;
                        case WM_MBUTTONDOWN:
                        case WM_MBUTTONUP:
                            event.code = SrtOverlayMouseButton_Middle;
                            break;
                        default:
                            event.code = (GET_XBUTTON_WPARAM(wParam) == XBUTTON1)
                                             ? SrtOverlayMouseButton_X1
                                             : SrtOverlayMouseButton_X2;
                            break;
                    }
                    Forward(event);
                    break;
                }

                case WM_MOUSEWHEEL:
                case WM_MOUSEHWHEEL:
                {
                    // Wheel messages carry SCREEN coordinates, unlike the button/move messages.
                    POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
                    {
                        std::lock_guard<std::mutex> lock(g_windowMutex);
                        if (g_window != nullptr)
                            ScreenToClient(g_window, &point);
                    }
                    SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_MouseWheel);
                    event.code = (msg == WM_MOUSEHWHEEL) ? 1u : 0u;
                    event.value = GET_WHEEL_DELTA_WPARAM(wParam);
                    event.x = point.x;
                    event.y = point.y;
                    Forward(event);
                    break;
                }

                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYDOWN:
                case WM_SYSKEYUP:
                {
                    SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_Key);
                    event.code = static_cast<uint32_t>(wParam);
                    event.value = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) ? 1 : 0;
                    event.x = static_cast<int32_t>((lParam >> 16) & 0xFF); // scan code
                    if (lParam & (1 << 24))
                        event.flags |= SRT_OVERLAY_INPUT_FLAG_EXTENDED;
                    if ((event.value == 1) && (lParam & (1 << 30)))
                        event.flags |= SRT_OVERLAY_INPUT_FLAG_REPEAT;
                    Forward(event);
                    break;
                }

                case WM_CHAR:
                case WM_SYSCHAR:
                {
                    SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_Char);
                    event.code = static_cast<uint32_t>(wParam); // one UTF-16 code unit
                    Forward(event);
                    break;
                }

                case WM_INPUT:
                {
                    UINT size = 0;
                    if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, nullptr, &size,
                                        sizeof(RAWINPUTHEADER)) != 0)
                        break;
                    if (size == 0 || size > 128)
                        break;

                    alignas(RAWINPUT) BYTE buffer[128];
                    if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, buffer, &size,
                                        sizeof(RAWINPUTHEADER)) != size)
                        break;

                    const RAWINPUT* raw = reinterpret_cast<const RAWINPUT*>(buffer);
                    if (raw->header.dwType == RIM_TYPEMOUSE)
                    {
                        const RAWMOUSE& mouse = raw->data.mouse;
                        SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_RawMouse);
                        event.x = mouse.lLastX;
                        event.y = mouse.lLastY;
                        event.code = mouse.usButtonFlags;
                        event.value = static_cast<int16_t>(mouse.usButtonData);
                        if (mouse.usFlags & MOUSE_MOVE_ABSOLUTE)
                            event.flags |= SRT_OVERLAY_INPUT_FLAG_ABSOLUTE;
                        Forward(event);
                    }
                    else if (raw->header.dwType == RIM_TYPEKEYBOARD)
                    {
                        const RAWKEYBOARD& key = raw->data.keyboard;
                        SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_RawKey);
                        event.code = key.VKey;
                        event.value = (key.Flags & RI_KEY_BREAK) ? 0 : 1;
                        event.x = key.MakeCode;
                        if (key.Flags & RI_KEY_E0)
                            event.flags |= SRT_OVERLAY_INPUT_FLAG_EXTENDED;
                        Forward(event);
                    }
                    break;
                }

                default:
                    break;
            }
        }

        // Is this a message we swallow from the game while interactive?
        bool IsCapturedInputMessage(UINT msg)
        {
            switch (msg)
            {
                case WM_MOUSEMOVE:
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                case WM_RBUTTONDBLCLK:
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                case WM_MBUTTONDBLCLK:
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                case WM_XBUTTONDBLCLK:
                case WM_MOUSEWHEEL:
                case WM_MOUSEHWHEEL:
                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYDOWN:
                case WM_SYSKEYUP:
                case WM_CHAR:
                case WM_SYSCHAR:
                    return true;
                default:
                    return false;
            }
        }

        LRESULT CALLBACK WndProcDetour(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
        {
            WNDPROC original = g_originalWndProc;

            // Hotkeys first, and always - they are ours whether or not the overlay is interactive, so the
            // game never sees F7/F8. Swallow both the down and up edges of a hotkey key.
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                const bool repeat = (lParam & (1 << 30)) != 0;
                if (HandleHotkey(wParam, /*keyDown*/ true, repeat))
                    return 0;
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                if (wParam == g_visibleHotkey || wParam == g_interactiveHotkey)
                    return 0;
            }

            // Focus tracking, so the consumer can release held keys when the game loses focus. Losing
            // focus also drops interactive: alt-tabbing out must not leave the game's input swallowed.
            if (msg == WM_KILLFOCUS)
            {
                g_windowFocused.store(false);
                if (g_interactive.exchange(false))
                    OverlayLog::Write("Input: window lost focus; interactive off.");
                SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_Focus);
                event.value = 0;
                Forward(event);
                PublishState();
            }
            else if (msg == WM_SETFOCUS)
            {
                g_windowFocused.store(true);
                SrtOverlayInputEvent event = MakeEvent(SrtOverlayInput_Focus);
                event.value = 1;
                Forward(event);
                PublishState();
            }

            if (g_interactive.load())
            {
                if (msg == WM_INPUT)
                {
                    CaptureMessage(msg, wParam, lParam);
                    // Raw input needs DefWindowProc for system cleanup even when the app consumes it; call
                    // it rather than the game's proc, so the game does not also see this event.
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
                }

                if (IsCapturedInputMessage(msg))
                {
                    CaptureMessage(msg, wParam, lParam);
                    return 0; // swallowed: the game does not see it while a panel is open
                }
            }

            return CallWindowProcW(original, hwnd, msg, wParam, lParam);
        }

        // ---- DirectInput8 ----

        HRESULT STDMETHODCALLTYPE GetDeviceStateDetour(IDirectInputDevice8W* device, DWORD cbData, LPVOID lpvData)
        {
            const HRESULT hr = g_originalGetDeviceState(device, cbData, lpvData);
            if (SUCCEEDED(hr) && lpvData != nullptr && g_interactive.load())
                std::memset(lpvData, 0, cbData); // freeze the device's polled state while a panel is open
            return hr;
        }

        HRESULT STDMETHODCALLTYPE GetDeviceDataDetour(IDirectInputDevice8W* device, DWORD cbObjectData,
                                                      LPDIDEVICEOBJECTDATA rgdod, LPDWORD pdwInOut, DWORD dwFlags)
        {
            const HRESULT hr = g_originalGetDeviceData(device, cbObjectData, rgdod, pdwInOut, dwFlags);
            if (SUCCEEDED(hr) && g_interactive.load() && pdwInOut != nullptr)
                *pdwInOut = 0; // report no buffered events while a panel is open
            return hr;
        }

        // Resolve the shared IDirectInputDevice8W vtable from a throwaway device. SEH-guarded and using
        // only raw pointers, so a COM fault here never takes the game down and no C++ unwinding is needed.
        void** ResolveDInputVTable(HMODULE dinput)
        {
            auto create = reinterpret_cast<decltype(&DirectInput8Create)>(GetProcAddress(dinput, "DirectInput8Create"));
            if (create == nullptr)
                return nullptr;

            __try
            {
                IDirectInput8W* directInput = nullptr;
                if (FAILED(create(GetModuleHandleW(nullptr), DIRECTINPUT_VERSION, IID_IDirectInput8W,
                                  reinterpret_cast<void**>(&directInput), nullptr)) ||
                    directInput == nullptr)
                    return nullptr;

                IDirectInputDevice8W* device = nullptr;
                if (FAILED(directInput->CreateDevice(GUID_SysKeyboard, &device, nullptr)) || device == nullptr)
                {
                    directInput->Release();
                    return nullptr;
                }

                void** vtable = *reinterpret_cast<void***>(device);
                device->Release();
                directInput->Release();
                return vtable;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return nullptr;
            }
        }

        void AttachDInput8()
        {
            HMODULE dinput = LoadLibraryW(L"dinput8.dll");
            if (dinput == nullptr)
            {
                OverlayLog::Write("Input: dinput8.dll is not present; DirectInput capture is off (raw input still covers the camera).");
                return;
            }
            // The ref is left held: dinput8 stays resident for the game's life anyway, and dropping it
            // would risk unmapping a module the game may start using.

            void** vtable = ResolveDInputVTable(dinput);
            if (vtable == nullptr)
            {
                OverlayLog::Write("Input: could not resolve the DirectInput8 device vtable; DirectInput capture is off.");
                return;
            }

            const bool state = g_getDeviceStateHook.Install("GetDeviceState", vtable, kSlot_GetDeviceState,
                                                            reinterpret_cast<void*>(&GetDeviceStateDetour));
            const bool data = g_getDeviceDataHook.Install("GetDeviceData", vtable, kSlot_GetDeviceData,
                                                          reinterpret_cast<void*>(&GetDeviceDataDetour));

            if (state)
                g_originalGetDeviceState = reinterpret_cast<PfnGetDeviceState>(g_getDeviceStateHook.Original());
            if (data)
                g_originalGetDeviceData = reinterpret_cast<PfnGetDeviceData>(g_getDeviceDataHook.Original());

            char line[96];
            std::snprintf(line, sizeof(line), "Input: DirectInput8 hooks installed: GetDeviceState=%d, GetDeviceData=%d.",
                          state ? 1 : 0, data ? 1 : 0);
            OverlayLog::Write(line);
        }
    }

    namespace OverlayInput
    {
        void Start(uint8_t visibleHotkey, uint8_t interactiveHotkey, OverlayPipeClient* pipe)
        {
            g_visibleHotkey = visibleHotkey != 0 ? visibleHotkey : SRT_OVERLAY_DEFAULT_VISIBLE_HOTKEY;
            g_interactiveHotkey = interactiveHotkey != 0 ? interactiveHotkey : SRT_OVERLAY_DEFAULT_INTERACTIVE_HOTKEY;
            g_pipe = pipe;

            g_visible.store(true);
            g_interactive.store(false);
            g_windowFocused.store(true);
            g_active.store(true);

            char line[160];
            std::snprintf(line, sizeof(line),
                          "Input: started. Visible hotkey VK 0x%02X, interactive hotkey VK 0x%02X. Overlay visible by default.",
                          g_visibleHotkey, g_interactiveHotkey);
            OverlayLog::Write(line);

            AttachDInput8();
        }

        void AttachWindow(HWND window)
        {
            if (window == nullptr)
                return;

            {
                std::lock_guard<std::mutex> lock(g_windowMutex);
                if (g_window == window)
                    return; // already hooked this window

                // A game that recreated its swapchain on a new window: put the old proc back first.
                if (g_window != nullptr && g_originalWndProc != nullptr)
                {
                    SetWindowLongPtrW(g_window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(g_originalWndProc));
                    g_originalWndProc = nullptr;
                }

                g_originalWndProc = reinterpret_cast<WNDPROC>(
                    SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&WndProcDetour)));
                g_window = window;
            }

            char line[96];
            std::snprintf(line, sizeof(line), "Input: hooked the game window 0x%p WndProc.", static_cast<void*>(window));
            OverlayLog::Write(line);

            // Publish the initial state so a consumer already connected knows we are visible and not
            // interactive. Outside the lock, because PublishState takes g_windowMutex itself.
            PublishState();
        }

        bool IsVisible()
        {
            return g_active.load() && g_visible.load();
        }

        bool IsInteractive()
        {
            return g_active.load() && g_interactive.load();
        }

        void Stop()
        {
            if (!g_active.exchange(false))
                return;

            // Put the window proc back before anything else, while the module is still mapped: a detour
            // left pointing into an unmapped shim would crash the game on its next message.
            {
                std::lock_guard<std::mutex> lock(g_windowMutex);
                if (g_window != nullptr && g_originalWndProc != nullptr)
                {
                    // Restore only if our detour is still installed - another tool may have chained after
                    // us, and tearing that out would point the slot at a proc it no longer expects.
                    const LONG_PTR current = GetWindowLongPtrW(g_window, GWLP_WNDPROC);
                    if (current == reinterpret_cast<LONG_PTR>(&WndProcDetour))
                        SetWindowLongPtrW(g_window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(g_originalWndProc));
                    else
                        OverlayLog::Write("Input: another WndProc hook chained after ours; leaving it in place.");
                }
                g_window = nullptr;
                g_originalWndProc = nullptr;
            }

            g_getDeviceDataHook.Uninstall();
            g_getDeviceStateHook.Uninstall();
            g_originalGetDeviceState = nullptr;
            g_originalGetDeviceData = nullptr;

            g_pipe = nullptr;
            OverlayLog::Write("Input: detached WndProc and DirectInput hooks.");
        }

        std::string DescribeActivity()
        {
            char buffer[160];
            std::snprintf(buffer, sizeof(buffer), "visible %d, interactive %d, focus %d, hotkeys %ld, events forwarded %ld",
                          g_visible.load() ? 1 : 0, g_interactive.load() ? 1 : 0, g_windowFocused.load() ? 1 : 0,
                          g_hotkeyPresses.load(), g_eventsForwarded.load());
            return buffer;
        }
    }
}
