#include "OverlayRuntime.h"

#include <windows.h>

#include <atomic>
#include <cstdio>
#include <cwchar>
#include <memory>
#include <string>

#include "CrashHandler.h"
#include "OverlayInput.h"
#include "OverlayLog.h"
#include "OverlayPipeClient.h"
#include "ShimModule.h"
#include "Text.h"

namespace srtoverlay::core
{
    namespace
    {
        // One shim per process - it is injected once - so the lifetime state is file-scope, exactly as
        // the C# spec keeps it static. A second injection is refused by the `started` latch.
        std::atomic<int> g_started{0};
        SrtOverlayStartupOptions g_options{};
        std::atomic<bool> g_haveOptions{false};

        IOverlayBackend* g_backend = nullptr;

        HANDLE g_ownerHandle = nullptr;
        HANDLE g_stopEvent = nullptr; // signalled by Stop to wake the watchdog and heartbeat
        // Every shim thread is a raw CreateThread (never std::thread): the shim is injected into a
        // foreign process, and FreeLibraryAndExitThread from a CRT-wrapped std::thread does not drop the
        // module refcount (see WatchdogProc). One thread model, no CRT thread wrapper, no surprises.
        HANDLE g_watchdogThread = nullptr;
        HANDLE g_heartbeatThread = nullptr;
        DWORD g_watchdogThreadId = 0;

        // The pipe back to the overlay consumer. Created only when the startup blob named a pipe; the
        // backend reads it through OverlayRuntime::Pipe(). A unique_ptr because its worker thread must be
        // started and joined across Start/Stop rather than living for the module's whole lifetime.
        std::unique_ptr<OverlayPipeClient> g_pipe;

        std::wstring OwnProcessFileName()
        {
            wchar_t buffer[MAX_PATH * 2];
            const DWORD length = GetModuleFileNameW(nullptr, buffer, static_cast<DWORD>(std::size(buffer)));
            if (length == 0)
                return {};

            std::wstring path(buffer, length);
            const size_t slash = path.find_last_of(L"\\/");
            return slash == std::wstring::npos ? path : path.substr(slash + 1);
        }

        void WriteIdentity(const SrtOverlayStartupOptions& options)
        {
            wchar_t shimPath[MAX_PATH * 2] = L"";
            GetModuleFileNameW(GetShimModule(), shimPath, static_cast<DWORD>(std::size(shimPath)));

            wchar_t hostPath[MAX_PATH * 2] = L"";
            GetModuleFileNameW(nullptr, hostPath, static_cast<DWORD>(std::size(hostPath)));

            char line[1024];
            std::snprintf(line, sizeof(line), "SRT Overlay shim (C++), protocol v%d.", SRT_OVERLAY_PROTOCOL_VERSION);
            OverlayLog::Write(line);

            OverlayLog::Write("  shim image   " + Narrow(shimPath) + " @ 0x" +
                              [] { char b[20]; std::snprintf(b, sizeof(b), "%p", static_cast<void*>(GetShimModule())); return std::string(b); }());
            OverlayLog::Write("  host process " + Narrow(hostPath) + " (pid " + std::to_string(GetCurrentProcessId()) + ")");
            OverlayLog::Write("  start thread " + std::to_string(GetCurrentThreadId()));
            OverlayLog::Write(std::string("  session      ") + (options.sessionId[0] ? Narrow(options.sessionId) : "<none>"));
            OverlayLog::Write(std::string("  pipe         ") + (options.pipeName[0] ? Narrow(options.pipeName) : "<none>"));
        }

        // The owner-watchdog thread body. Deliberately a RAW thread (CreateThread), NOT a std::thread,
        // and it is this thread that calls FreeLibraryAndExitThread to self-unload.
        //
        // This is load-bearing: FreeLibraryAndExitThread called from a CRT-wrapped std::thread
        // (_beginthreadex) does NOT drop the module's reference count - the shim stays mapped after the
        // "unload", so a rebuilt shim cannot be reinjected over it (LoadLibraryW returns the stale
        // resident module). From a raw OS thread the same call unmaps cleanly. This matches the RE9
        // reference, whose FreeLibraryAndExitThread runs on its raw injected thread. Measured on
        // 2026-09-12: std::thread left the module resident; a raw thread takes it to zero.
        DWORD WINAPI WatchdogProc(LPVOID)
        {
            g_watchdogThreadId = GetCurrentThreadId();
            HANDLE waits[2] = {g_stopEvent, g_ownerHandle};
            const DWORD result = WaitForMultipleObjects(2, waits, FALSE, INFINITE);

            if (result == WAIT_OBJECT_0)
                return 0; // Stop() signalled us; a normal detach, nothing to do.

            // The owner exited. Tear down and unload ourselves - there is nobody left to ask.
            OverlayLog::Write("Owner process exited; detaching and unloading.");
            OverlayRuntime::Stop();

            HMODULE self = GetShimModule();
            if (self != nullptr)
                FreeLibraryAndExitThread(self, 0); // never returns; unmaps the shim cleanly

            return 0;
        }

        // Detach automatically if the process that injected this shim goes away. Once Present is hooked
        // an orphaned shim is a detour in someone's game with nothing alive that could remove it -
        // strictly worse than never having injected - so this is a safety net, not a nicety. On the
        // owner-death path the shim genuinely unloads (FreeLibraryAndExitThread), which the C# NativeAOT
        // shim could never do: this is reason 1 for the C++ move.
        void WatchOwner(int ownerProcessId)
        {
            if (ownerProcessId == 0)
            {
                OverlayLog::Write("No owner process given; the overlay will only detach when told to.");
                return;
            }

            g_ownerHandle = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(ownerProcessId));
            if (g_ownerHandle == nullptr)
            {
                OverlayLog::Write("Could not open owner process " + std::to_string(ownerProcessId) +
                                  " to watch it; the overlay will only detach when told to.");
                return;
            }

            g_watchdogThread = CreateThread(nullptr, 0, &WatchdogProc, nullptr, 0, nullptr);
            if (g_watchdogThread == nullptr)
            {
                OverlayLog::Write("Could not create the owner-watchdog thread; the overlay will only detach when told to.");
                CloseHandle(g_ownerHandle);
                g_ownerHandle = nullptr;
                return;
            }

            OverlayLog::Write("  owner        pid " + std::to_string(ownerProcessId) + ", watched");
        }

        // Connect the pipe back to the overlay consumer, if the startup blob named one. Started before
        // the backend installs its hooks, so the compositor's first Present-driven initialise already
        // has a pipe to send its surface request down. An empty pipe name is the notepad / injection-
        // spike case: hooks go in, but there is no consumer and nothing is composited.
        void StartPipe(const SrtOverlayStartupOptions& options)
        {
            if (options.pipeName[0] == L'\0')
            {
                OverlayLog::Write("No pipe name in the startup options; the overlay will hook but composite nothing.");
                return;
            }

            g_pipe = std::make_unique<OverlayPipeClient>();
            g_pipe->Start(options.pipeName);
        }

        // The heartbeat thread body (raw thread, like every shim thread). It is the only way to observe
        // a hook that draws nothing: it formats and writes on a timer, far from the render path. Wakes
        // and exits when Stop() signals g_stopEvent.
        DWORD WINAPI HeartbeatProc(LPVOID)
        {
            IOverlayBackend* active = g_backend;
            if (active == nullptr)
                return 0;

            while (WaitForSingleObject(g_stopEvent, 5000) == WAIT_TIMEOUT)
                OverlayLog::Write("[" + std::string(active->Name()) + "] " + active->DescribeActivity());

            return 0;
        }

        // Bring a backend up, and start the heartbeat that is the only way to observe a hook that draws
        // nothing. The heartbeat formats and writes - both banned on the render path - far away from it.
        void StartBackend(IOverlayBackend* backend)
        {
            if (backend == nullptr)
                return;

            bool ready = false;
            try
            {
                ready = backend->Initialize();
            }
            catch (...)
            {
                OverlayLog::Write("Backend initialisation threw; continuing without it.");
                ready = false;
            }

            if (!ready)
            {
                OverlayLog::Write(std::string(backend->Name()) + " is not usable in this process; continuing without it.");
                return;
            }

            g_backend = backend;

            g_heartbeatThread = CreateThread(nullptr, 0, &HeartbeatProc, nullptr, 0, nullptr);
        }

        SrtOverlayStartResult Refuse(SrtOverlayStartResult result)
        {
            g_started.store(0);
            OverlayLog::Close();
            return result;
        }
    }

    namespace OverlayRuntime
    {
        SrtOverlayStartResult Start(const SrtOverlayStartupOptions* options, IOverlayBackend* backend)
        {
            int expected = 0;
            if (!g_started.compare_exchange_strong(expected, 1))
                return SrtOverlayStartResult_AlreadyRunning;

            if (options == nullptr)
                return SrtOverlayStartResult_BadArgument;

            g_options = *options;
            g_haveOptions.store(true);
            const SrtOverlayStartupOptions& opt = g_options;

            if (opt.logPath[0] != L'\0')
                OverlayLog::Open(opt.logPath);

            if (opt.protocolVersion != SRT_OVERLAY_PROTOCOL_VERSION)
            {
                OverlayLog::Write("Refusing to start: startup blob states protocol version " +
                                  std::to_string(opt.protocolVersion) + ", this shim implements " +
                                  std::to_string(SRT_OVERLAY_PROTOCOL_VERSION) + ".");
                return Refuse(SrtOverlayStartResult_ProtocolMismatch);
            }

            // The mis-aimed-injection guard from section 11. A string compare, before any hook is
            // installed, so a shim that landed in the wrong process draws over nothing and leaves.
            const std::wstring actual = OwnProcessFileName();
            if (_wcsicmp(actual.c_str(), opt.targetProcess) != 0)
            {
                OverlayLog::Write("Refusing to start: this process is '" + Narrow(actual) +
                                  "', the startup blob named '" + Narrow(opt.targetProcess) + "'.");
                return Refuse(SrtOverlayStartResult_WrongProcess);
            }

            g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr); // manual-reset: both threads see it
            if (g_stopEvent == nullptr)
                return Refuse(SrtOverlayStartResult_Failed);

            WriteIdentity(opt);

            // Install the crash handler before any hook goes in, so a fault on the very first hooked
            // Present still leaves a minidump. It writes beside the shim log and appends a note to it.
            CrashHandler::Install(GetShimModule(), opt.logPath);

            StartPipe(opt);

            // Input capture is backend-agnostic and forwards over the pipe, so it starts after the pipe
            // and before the backend. It hooks DirectInput8 now; the game window is hooked later, once the
            // backend has resolved it from the swapchain (OverlayInput::AttachWindow).
            OverlayInput::Start(opt.visibleHotkey, opt.interactiveHotkey, g_pipe.get());

            StartBackend(backend);

            // The owner watchdog starts LAST, after the hooks are installed. If it started earlier and
            // the owner died mid-startup, its Stop() would run concurrently with StartBackend still
            // installing hooks: the unhook would find nothing, then the hooks would be installed and
            // never removed, and FreeLibraryAndExitThread would unmap the shim with the game's vtable
            // still pointing into it - which then crashes a same-base re-injection (VTableHook::Install
            // sees its own stale detour and refuses). Starting it here means an owner already gone is
            // caught immediately, but only once the hooks are in and can be cleanly removed.
            WatchOwner(opt.ownerProcessId);

            OverlayLog::Write(g_backend == nullptr
                                  ? "Overlay started with no graphics backend; nothing is hooked."
                                  : std::string("Overlay started on ") + g_backend->Name() + ".");

            return SrtOverlayStartResult_Success;
        }

        void Stop()
        {
            if (g_started.exchange(0) == 0)
                return;

            // Wake the watchdog and heartbeat. Manual-reset, so both observe it.
            if (g_stopEvent != nullptr)
                SetEvent(g_stopEvent);

            // Join the heartbeat (never runs Stop itself). Wait on the watchdog unless we ARE it - the
            // owner-death path calls Stop from inside the watchdog thread, where waiting on ourselves
            // would deadlock; there we just drop the handle and let the proc go on to
            // FreeLibraryAndExitThread.
            if (g_heartbeatThread != nullptr)
            {
                WaitForSingleObject(g_heartbeatThread, INFINITE);
                CloseHandle(g_heartbeatThread);
                g_heartbeatThread = nullptr;
            }

            if (g_watchdogThread != nullptr)
            {
                if (GetCurrentThreadId() != g_watchdogThreadId)
                    WaitForSingleObject(g_watchdogThread, INFINITE);
                CloseHandle(g_watchdogThread);
                g_watchdogThread = nullptr;
            }

            // Hooks come out before anything else, because everything else exists to serve them and the
            // game may be calling through them right now.
            IOverlayBackend* backend = g_backend;
            g_backend = nullptr;
            if (backend != nullptr)
            {
                try
                {
                    backend->Shutdown();
                }
                catch (...)
                {
                    OverlayLog::Write("Backend shutdown threw.");
                }
            }

            // Input hooks (WndProc + DirectInput) come out next, before the pipe they forward over is
            // stopped. Like the backend's hooks, they must be removed while the module is still mapped.
            OverlayInput::Stop();

            // The backend's hooks are out, so no render-thread reader can touch the pipe any more; stop
            // its worker and close the pipe.
            if (g_pipe)
            {
                g_pipe->Stop();
                g_pipe.reset();
            }

            // Restore the previous exception filter and stop the crash worker before the module can be
            // FreeLibrary'd - otherwise the game would be left calling into our unmapped code.
            CrashHandler::Uninstall();

            if (g_ownerHandle != nullptr)
            {
                CloseHandle(g_ownerHandle);
                g_ownerHandle = nullptr;
            }
            if (g_stopEvent != nullptr)
            {
                CloseHandle(g_stopEvent);
                g_stopEvent = nullptr;
            }

            g_haveOptions.store(false);
            OverlayLog::Write("Overlay detached. The injector may now FreeLibrary the module.");
            OverlayLog::Close();
        }

        const SrtOverlayStartupOptions* Options()
        {
            return g_haveOptions.load() ? &g_options : nullptr;
        }

        OverlayPipeClient* Pipe()
        {
            return g_pipe.get();
        }
    }
}
