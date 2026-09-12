// The shim's client end of the runner-to-shim overlay pipe.
//
// The overlay plugin's runner creates the pipe server and injects the shim with the pipe's name; the
// shim connects here as the client. Two messages cross it (OverlayProtocol.h): the shim sends one
// SrtOverlaySurfaceRequest naming the surface and adapter it wants, and the consumer replies with a
// SrtOverlaySurfacePublish every completed frame. The textures and the fence themselves still cross by
// name; this pipe only carries the negotiation the step-3 shared-memory blocks used to.
//
// Threading contract, because half of this is touched from the game's render thread:
//   * Start/Stop and SetSurfaceRequest run on the shim's own threads (never the render thread, except
//     SetSurfaceRequest on the rare ResizeBuffers path).
//   * TryGetLatestPublish runs on the render thread inside the hooked Present, so it NEVER blocks: it
//     is a bounded-spin seqlock read of the last publish the worker thread stored.
//   * A single worker thread owns the pipe handle: it connects, sends the pending request, and reads
//     publishes. All blocking I/O is overlapped and cancelled by a stop event, so Stop never hangs.

#ifndef SRTOVERLAY_PIPE_CLIENT_H
#define SRTOVERLAY_PIPE_CLIENT_H

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>

#include "OverlayProtocol.h"

namespace srtoverlay::core
{
    class OverlayPipeClient
    {
    public:
        OverlayPipeClient() = default;
        ~OverlayPipeClient();

        OverlayPipeClient(const OverlayPipeClient&) = delete;
        OverlayPipeClient& operator=(const OverlayPipeClient&) = delete;

        // Start the worker that connects to \\.\pipe\<pipeName> (name without the prefix) and reads
        // publishes. Returns immediately; connection happens on the worker. A second call is ignored.
        void Start(const std::wstring& pipeName);

        // Set, or update on a resize, the surface request to send the consumer. Only records the
        // request and wakes the worker; the worker thread does the actual send, so this NEVER blocks on
        // I/O even though the compositor calls it from the game's render thread. Thread-safe.
        void SetSurfaceRequest(const SrtOverlaySurfaceRequest& request);

        // Snapshot the newest publish the worker has read. Returns false if none has arrived yet or the
        // consumer reported not-alive. Non-blocking; safe on the render thread.
        bool TryGetLatestPublish(SrtOverlaySurfacePublish& out) const;

        // Whether the pipe is currently connected to a consumer.
        bool IsConnected() const { return connected_.load(std::memory_order_acquire); }

        // One line for the heartbeat log. Off the render path.
        std::string DescribeActivity() const;

        // Stop the worker and close the pipe. Safe to call more than once; must not be called from the
        // worker thread itself.
        void Stop();

    private:
        enum class SendResult { Nothing, Sent, Failed };

        // A raw CreateThread entry point (every shim thread is a raw thread, never std::thread); it just
        // calls Worker() on the instance passed as the thread parameter.
        static unsigned long __stdcall WorkerThunk(void* self);

        void Worker();
        bool ConnectWithRetry();
        SendResult SendPendingIfAny();                             // worker: send the request if one is unsent
        bool WaitAndSendFirstRequest();                             // worker: block until the first request, send it
        void TrySendPendingRequest();                              // worker: send a newer (resize) request if any
        bool WriteFrame(uint16_t kind, const void* payload, uint32_t size); // worker thread only
        bool ReadExact(uint8_t* buffer, uint32_t length);          // worker thread; overlapped, stop-aware
        void StorePublish(const SrtOverlaySurfacePublish& publish); // seqlock write
        void ClosePipe();

        std::wstring pipeName_;
        HANDLE pipe_ = INVALID_HANDLE_VALUE;
        HANDLE stopEvent_ = nullptr;    // manual-reset: wakes every wait to shut down
        HANDLE requestEvent_ = nullptr; // auto-reset: SetSurfaceRequest wakes the worker to send
        HANDLE readEvent_ = nullptr;    // OVERLAPPED event for reads, worker thread only
        HANDLE writeEvent_ = nullptr;   // OVERLAPPED event for writes, worker thread only
        HANDLE workerThread_ = nullptr; // raw CreateThread; see WorkerThunk

        std::atomic<bool> running_{false};
        std::atomic<bool> connected_{false};

        // The pending surface request. The render thread stores it under requestMutex_ and bumps
        // requestGen_; the worker reads it, writes it, and records the generation it sent so a request
        // that changed during the write is resent rather than falsely marked delivered. No I/O happens
        // under this lock, so the render thread's SetSurfaceRequest never blocks on the pipe.
        std::mutex requestMutex_;
        SrtOverlaySurfaceRequest request_{};
        bool hasRequest_ = false;
        uint64_t requestGen_ = 0;
        uint64_t sentGen_ = 0;

        // Seqlock guarding publish_: the worker writes it (seq odd during the copy, even after), the
        // render thread reads a stable snapshot. A 16-byte struct is not lock-free to load atomically on
        // x86-32, and the render thread must not take a lock, so a seqlock is the fit.
        mutable std::atomic<uint32_t> publishSeq_{0};
        SrtOverlaySurfacePublish publish_{};
        std::atomic<bool> hasPublish_{false};

        std::atomic<long> publishesReceived_{0};
        std::atomic<long> connectAttempts_{0};
    };
}

#endif // SRTOVERLAY_PIPE_CLIENT_H
