#include "D3D12Backend.h"

#include <windows.h>

#include <d3d12.h>
#include <dxgi1_4.h>

#include <atomic>
#include <cstdio>
#include <memory>

#include "D3D12Compositor.h"
#include "D3D12Slots.h"
#include "D3D12VTableResolver.h"
#include "OverlayLog.h"
#include "OverlayRuntime.h"
#include "VTableHook.h"

using srtoverlay::core::OverlayLog;
using srtoverlay::core::VTableHook;

namespace srtoverlay::directx12
{
    namespace
    {
        // The reference waits 5 x back-buffer-count Presents before initialising, so a DIRECT queue
        // has certainly been observed. The count is not known until the swapchain is in hand, so a
        // fixed upper bound (5 x 4) is used; it costs a few frames of delay and nothing else.
        constexpr long kFramesUntilInit = 20;

        using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, UINT, UINT);
        using ResizeBuffersFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
        using ExecuteCommandListsFn = void(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);

        PresentFn g_originalPresent = nullptr;
        ResizeBuffersFn g_originalResizeBuffers = nullptr;
        ExecuteCommandListsFn g_originalExecuteCommandLists = nullptr;

        VTableHook g_presentHook;
        VTableHook g_resizeBuffersHook;
        VTableHook g_executeCommandListsHook;

        std::atomic<long> g_presentCount{0};
        std::atomic<long> g_resizeBuffersCount{0};
        std::atomic<long> g_executeCommandListsCount{0};
        std::atomic<void*> g_lastCommandQueue{nullptr};
        std::atomic<void*> g_lastSwapChain{nullptr};

        // The game's rendering queue, learned from ExecuteCommandLists and read by Present to initialise
        // the compositor on.
        std::atomic<ID3D12CommandQueue*> g_capturedDirectQueue{nullptr};
        std::atomic<long> g_framesSeen{0};

        // Queue pointers already probed for their type, so GetDesc runs a handful of times rather than
        // on every submit. Best-effort and lock-free: a race can re-probe a queue, which is harmless.
        constexpr int kMaxTestedQueues = 16;
        std::atomic<ID3D12CommandQueue*> g_testedQueues[kMaxTestedQueues]{};
        std::atomic<int> g_testedCount{0};

        std::unique_ptr<D3D12Compositor> g_compositor;
        std::atomic<bool> g_compositorEnabled{false};
        std::atomic<int> g_compositorState{0}; // 0 not initialised, 1 ready, 2 failed
        std::atomic<bool> g_resizePending{false};

        bool ShouldProbe(ID3D12CommandQueue* queue)
        {
            const int count = g_testedCount.load();
            for (int i = 0; i < count; ++i)
            {
                if (g_testedQueues[i].load() == queue)
                    return false;
            }

            if (count >= kMaxTestedQueues)
                return false; // give up probing rather than grow unbounded; a DIRECT queue was surely seen

            g_testedQueues[count].store(queue);
            g_testedCount.store(count + 1);
            return true;
        }

        void DriveCompositor(IDXGISwapChain3* swapChain, UINT flags)
        {
            if (!g_compositorEnabled.load() || g_compositorState.load() == 2)
                return;

            D3D12Compositor* compositor = g_compositor.get();
            if (compositor == nullptr)
                return;

            // Rebuild after a resize before anything else touches the swapchain's buffers.
            if (g_resizePending.load())
            {
                if (compositor->Reinitialize(swapChain))
                    g_resizePending.store(false);
                else
                    g_compositorState.store(2);
                return;
            }

            if (g_compositorState.load() == 0)
            {
                // Wait until a DIRECT queue has certainly been seen, then initialise on it.
                if (g_framesSeen.fetch_add(1) + 1 <= kFramesUntilInit)
                    return;

                ID3D12CommandQueue* queue = g_capturedDirectQueue.load();
                if (queue == nullptr)
                    return;

                g_compositorState.store(compositor->Initialize(swapChain, queue) ? 1 : 2);

                // Skip compositing on the very frame we initialised on: the swapchain is mid-Present.
                return;
            }

            if ((flags & DXGI_PRESENT_TEST) != 0)
                return; // a test present validates the swapchain without showing anything

            compositor->Composite(swapChain);
        }

        HRESULT STDMETHODCALLTYPE PresentDetour(IDXGISwapChain3* swapChain, UINT syncInterval, UINT flags)
        {
            g_presentCount.fetch_add(1);
            g_lastSwapChain.store(swapChain);

            try
            {
                DriveCompositor(swapChain, flags);
            }
            catch (...)
            {
                // No exception may cross this boundary. A fault disables compositing rather than
                // risking a repeat; the original is still called below so the game presents normally.
                g_compositorState.store(2);
            }

            return g_originalPresent(swapChain, syncInterval, flags);
        }

        HRESULT STDMETHODCALLTYPE ResizeBuffersDetour(IDXGISwapChain3* swapChain, UINT bufferCount, UINT width,
                                                      UINT height, DXGI_FORMAT format, UINT flags)
        {
            g_resizeBuffersCount.fetch_add(1);

            try
            {
                if (g_compositorEnabled.load() && g_compositorState.load() == 1 && g_compositor)
                {
                    g_compositor->PrepareForResize();
                    g_resizePending.store(true);
                    g_compositorState.store(0); // re-run init (which now takes the Reinitialize branch)
                }
            }
            catch (...)
            {
                g_compositorState.store(2);
            }

            return g_originalResizeBuffers(swapChain, bufferCount, width, height, format, flags);
        }

        void STDMETHODCALLTYPE ExecuteCommandListsDetour(ID3D12CommandQueue* commandQueue, UINT numCommandLists,
                                                         ID3D12CommandList* const* commandLists)
        {
            g_executeCommandListsCount.fetch_add(1);
            g_lastCommandQueue.store(commandQueue);

            try
            {
                if (g_compositorEnabled.load() && g_capturedDirectQueue.load() == nullptr &&
                    ShouldProbe(commandQueue) && commandQueue->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT)
                {
                    ID3D12CommandQueue* expected = nullptr;
                    g_capturedDirectQueue.compare_exchange_strong(expected, commandQueue);
                }
            }
            catch (...)
            {
                // Reading the descriptor must never take the game down; worst case the queue is
                // captured on the next call.
            }

            g_originalExecuteCommandLists(commandQueue, numCommandLists, commandLists);
        }

        void Uninstall(VTableHook& hook)
        {
            if (hook.Empty())
                return;

            const std::string name = hook.Name();
            const bool removed = hook.Uninstall();
            OverlayLog::Write(removed
                                  ? "D3D12: " + name + " hook removed."
                                  : "D3D12: " + name + " hook could NOT be removed - something else replaced the "
                                                       "slot after us, and tearing that out would point it at a "
                                                       "function it no longer expects.");
        }
    }

    bool D3D12Backend::Initialize()
    {
        const SrtOverlayStartupOptions* options = core::OverlayRuntime::Options();
        const wchar_t* session = options != nullptr ? options->sessionId : L"";

        if (session != nullptr && session[0] != L'\0')
        {
            g_compositor = std::make_unique<D3D12Compositor>(session);
            g_compositorEnabled.store(true);
        }
        else
        {
            OverlayLog::Write("D3D12: no session id in the startup options; hooks only, no compositing.");
        }

        const ResolvedVTables vtables = ResolveVTables();
        if (!vtables.IsComplete())
        {
            OverlayLog::Write("D3D12: vtable resolution produced nothing; not hooking.");
            return false;
        }

        {
            char line[128];
            std::snprintf(line, sizeof(line), "D3D12 vtables resolved: swapchain 0x%p, command queue 0x%p.",
                          static_cast<void*>(vtables.swapChainVTable), static_cast<void*>(vtables.commandQueueVTable));
            OverlayLog::Write(line);
        }

        const bool present = g_presentHook.Install("Present", vtables.swapChainVTable,
                                                    kSlot_IDXGISwapChain_Present, reinterpret_cast<void*>(&PresentDetour));
        const bool resize = g_resizeBuffersHook.Install("ResizeBuffers", vtables.swapChainVTable,
                                                        kSlot_IDXGISwapChain_ResizeBuffers, reinterpret_cast<void*>(&ResizeBuffersDetour));
        const bool execute = g_executeCommandListsHook.Install("ExecuteCommandLists", vtables.commandQueueVTable,
                                                              kSlot_ID3D12CommandQueue_ExecuteCommandLists, reinterpret_cast<void*>(&ExecuteCommandListsDetour));

        if (present)
            g_originalPresent = reinterpret_cast<PresentFn>(g_presentHook.Original());
        if (resize)
            g_originalResizeBuffers = reinterpret_cast<ResizeBuffersFn>(g_resizeBuffersHook.Original());
        if (execute)
            g_originalExecuteCommandLists = reinterpret_cast<ExecuteCommandListsFn>(g_executeCommandListsHook.Original());

        {
            char line[128];
            std::snprintf(line, sizeof(line), "D3D12 hooks installed: Present=%d, ResizeBuffers=%d, ExecuteCommandLists=%d.",
                          present ? 1 : 0, resize ? 1 : 0, execute ? 1 : 0);
            OverlayLog::Write(line);
        }

        if (!present)
        {
            OverlayLog::Write("D3D12: the Present slot could not be written. Nothing is hooked; the game is untouched.");
            Shutdown();
            return false;
        }

        return true;
    }

    void D3D12Backend::Shutdown()
    {
        // Hooks first: the game may be calling through them right now, and everything else exists to
        // serve them. Once they are out, no NEW detour can reach the compositor being torn down.
        Uninstall(g_executeCommandListsHook);
        Uninstall(g_resizeBuffersHook);
        Uninstall(g_presentHook);

        g_compositorEnabled.store(false);

        // Let any detour already in flight when we unhooked finish before the compositor is freed. New
        // calls now reach the original directly; an in-flight Composite finishes in microseconds. This
        // is the "unhook, settle, release" the reference does and step 2 confirmed safe under load.
        Sleep(100);

        g_compositor.reset();
        g_compositorState.store(0);
        g_capturedDirectQueue.store(nullptr);
        g_framesSeen.store(0);
        g_testedCount.store(0);
    }

    std::string D3D12Backend::DescribeActivity()
    {
        const int state = g_compositorState.load();
        const char* stateText = state == 1 ? "ready" : (state == 2 ? "failed" : "pending");
        const std::string composite = g_compositor ? g_compositor->DescribeActivity() : std::string("no compositor");

        char buffer[512];
        std::snprintf(buffer, sizeof(buffer),
                      "Present %ld (swapchain 0x%p), ResizeBuffers %ld, ExecuteCommandLists %ld (queue 0x%p), "
                      "compositor %s [%s], Present hook still installed: %d",
                      g_presentCount.load(), g_lastSwapChain.load(), g_resizeBuffersCount.load(),
                      g_executeCommandListsCount.load(), g_lastCommandQueue.load(),
                      stateText, composite.c_str(), g_presentHook.IsInstalled() ? 1 : 0);
        return buffer;
    }
}
