#include "OverlayRuntime.h"

#include <windows.h>

#include <atomic>
#include <cstdio>
#include <cwchar>
#include <string>
#include <thread>

#include "OverlayLog.h"
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
        std::thread g_watchdog;
        std::thread g_heartbeat;
        DWORD g_watchdogThreadId = 0;

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

            g_watchdog = std::thread([]
            {
                g_watchdogThreadId = GetCurrentThreadId();
                HANDLE waits[2] = {g_stopEvent, g_ownerHandle};
                const DWORD result = WaitForMultipleObjects(2, waits, FALSE, INFINITE);

                if (result == WAIT_OBJECT_0)
                    return; // Stop() signalled us; a normal detach, nothing to do.

                // The owner exited. Tear down and unload ourselves - there is nobody left to ask.
                OverlayLog::Write("Owner process exited; detaching and unloading.");
                OverlayRuntime::Stop();

                HMODULE self = GetShimModule();
                if (self != nullptr)
                    FreeLibraryAndExitThread(self, 0); // never returns; unmaps the shim cleanly
            });

            OverlayLog::Write("  owner        pid " + std::to_string(ownerProcessId) + ", watched");
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

            g_heartbeat = std::thread([]
            {
                IOverlayBackend* active = g_backend;
                if (active == nullptr)
                    return;

                while (WaitForSingleObject(g_stopEvent, 5000) == WAIT_TIMEOUT)
                    OverlayLog::Write("[" + std::string(active->Name()) + "] " + active->DescribeActivity());
            });
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
            WatchOwner(opt.ownerProcessId);
            StartBackend(backend);

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

            // Join the heartbeat (never runs Stop itself). Join the watchdog unless we ARE it - the
            // owner-death path calls Stop from inside the watchdog thread, where a join would deadlock.
            if (g_heartbeat.joinable())
                g_heartbeat.join();

            if (g_watchdog.joinable())
            {
                if (GetCurrentThreadId() == g_watchdogThreadId)
                    g_watchdog.detach();
                else
                    g_watchdog.join();
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
    }
}
