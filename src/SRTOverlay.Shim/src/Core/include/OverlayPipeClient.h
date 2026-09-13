// The shim's client end of the runner-to-shim overlay pipe.
//
// The overlay plugin's runner creates the pipe server and injects the shim with the pipe's name; the
// shim connects here as the client. The pipe is duplex and carries four messages (OverlayProtocol.h):
//   shim -> consumer:  SrtOverlaySurfaceRequest, SrtOverlayInputBatch, SrtOverlayState
//   consumer -> shim:  SrtOverlaySurfacePublish
// The textures and the fence themselves still cross by name; this pipe carries the negotiation the
// step-3 shared-memory blocks used to (step 5) plus the forwarded input and state (step 6).
//
// Threading contract, because parts of this are touched from the game's own threads:
//   * Start/Stop run on the shim's start/detach threads.
//   * TryGetLatestPublish runs on the render thread inside the hooked Present, so it NEVER blocks: it
//     is a bounded-spin seqlock read of the last publish the worker thread stored.
//   * SetSurfaceRequest is called from the render thread on the rare ResizeBuffers path; it only
//     records the request and wakes the worker, so it never blocks on I/O.
//   * EnqueueInput and SetOverlayState are called from the game's window/UI thread (the WndProc and
//     DInput detours); they only enqueue under a short lock and wake the worker, never doing I/O.
//   * A single worker thread owns the pipe handle: it connects, and then both reads publishes and
//     writes the outbound messages, all overlapped and cancellable by a stop event, so Stop never
//     hangs. Reads and writes are independent directions of the duplex pipe.

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

        // Start the worker that connects to \\.\pipe\<pipeName> (name without the prefix), reads
        // publishes and writes the outbound messages. Returns immediately; connection happens on the
        // worker. A second call is ignored.
        void Start(const std::wstring& pipeName);

        // Set, or update on a resize, the surface request to send the consumer. Only records the
        // request and wakes the worker; the worker thread does the actual send, so this NEVER blocks on
        // I/O even though the compositor calls it from the game's render thread. Thread-safe.
        void SetSurfaceRequest(const SrtOverlaySurfaceRequest& request);

        // Queue one captured input event for forwarding to the consumer while interactive. Bounded: if
        // the queue is full the event is dropped and a dropped-count is carried in the next batch's
        // header so the consumer can resync held keys. Only records and wakes the worker; never blocks.
        // Called from the game's window thread. Thread-safe.
        void EnqueueInput(const SrtOverlayInputEvent& event);

        // Set the overlay state to forward to the consumer (visible/interactive/focus). Only records the
        // latest state and wakes the worker; the worker sends it. Coalescing is intentional - only the
        // newest state matters. Called from the game's window thread. Thread-safe.
        void SetOverlayState(const SrtOverlayState& state);

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
        void RunSession();                                          // read publishes + write outbound until either fails
        bool DrainOutput();                                         // worker: send any pending request, state, input
        SendResult SendPendingRequest();                            // worker: send the request if one is unsent
        bool DispatchInboundFrame(const struct IpcFrameHeader& header, const uint8_t* payload); // worker
        bool WriteFrame(uint16_t kind, const void* payload, uint32_t size); // worker thread only
        void StorePublish(const SrtOverlaySurfacePublish& publish); // seqlock write
        void OnDisconnected();                                      // clear per-connection state; resend request/state next time
        void ClosePipe();

        std::wstring pipeName_;
        HANDLE pipe_ = INVALID_HANDLE_VALUE;
        HANDLE stopEvent_ = nullptr;    // manual-reset: wakes every wait to shut down
        HANDLE outputEvent_ = nullptr;  // auto-reset: SetSurfaceRequest/EnqueueInput/SetOverlayState wake the worker to send
        HANDLE readEvent_ = nullptr;    // OVERLAPPED event for reads, worker thread only
        HANDLE writeEvent_ = nullptr;   // OVERLAPPED event for writes, worker thread only
        HANDLE workerThread_ = nullptr; // raw CreateThread; see WorkerThunk

        std::atomic<bool> running_{false};
        std::atomic<bool> connected_{false};

        // The pending surface request. Stored under requestMutex_ and marked unsent by bumping
        // requestGen_ past sentGen_; the worker reads it, writes it, and records the generation it sent
        // so a request that changed during the write is resent rather than falsely marked delivered. No
        // I/O happens under this lock, so the caller's SetSurfaceRequest never blocks on the pipe.
        std::mutex requestMutex_;
        SrtOverlaySurfaceRequest request_{};
        bool hasRequest_ = false;
        uint64_t requestGen_ = 0;
        uint64_t sentGen_ = 0;

        // The outbound input queue and the latest overlay state, both filled from the game's window
        // thread and drained by the worker. A short lock rather than lock-free: the window thread is not
        // the render thread, so a brief mutex there is fine, and it keeps the ring and the state
        // coalescing obviously correct.
        static constexpr int kInputCapacity = 256;
        std::mutex outputMutex_;
        SrtOverlayInputEvent inputRing_[kInputCapacity]{};
        int inputHead_ = 0;
        int inputCount_ = 0;
        uint32_t inputDropped_ = 0;
        SrtOverlayState state_{};
        bool hasState_ = false;
        bool stateDirty_ = false;

        // Seqlock guarding publish_: the worker writes it (seq odd during the copy, even after), the
        // render thread reads a stable snapshot. A 16-byte struct is not lock-free to load atomically on
        // x86-32, and the render thread must not take a lock, so a seqlock is the fit.
        mutable std::atomic<uint32_t> publishSeq_{0};
        SrtOverlaySurfacePublish publish_{};
        std::atomic<bool> hasPublish_{false};

        std::atomic<long> publishesReceived_{0};
        std::atomic<long> inputSent_{0};
        std::atomic<long> connectAttempts_{0};
    };
}

#endif // SRTOVERLAY_PIPE_CLIENT_H
