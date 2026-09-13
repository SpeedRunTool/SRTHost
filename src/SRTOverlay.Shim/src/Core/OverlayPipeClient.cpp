#include "OverlayPipeClient.h"

#include <cstring>

#include "IpcFrame.h"
#include "OverlayLog.h"
#include "Text.h"

namespace srtoverlay::core
{
    namespace
    {
        // How long the worker waits for the consumer's pipe server to exist before giving up on a
        // connection attempt. The consumer creates the server before it injects the shim, so a connect
        // normally succeeds at once; this is slack for a slow start, not a health check.
        constexpr DWORD kConnectTimeoutMs = 15000;
        constexpr DWORD kConnectRetryMs = 100;

        // Bounded spin for the seqlock read on the render thread. The writer holds the lock for a 16-byte
        // copy - nanoseconds - and publishes at frame rate, so a read never loses more than a try or two;
        // the bound only guarantees the render thread cannot spin forever if something pathological
        // happens, in which case it simply skips compositing this frame.
        constexpr int kSeqlockReadTries = 16;

        // The largest inbound payload the worker will buffer. Only SrtOverlaySurfacePublish (16 bytes)
        // travels consumer -> shim; anything larger is a malformed or hostile frame and tears the
        // connection down rather than being trusted.
        constexpr uint32_t kMaxInboundPayload = 256;

        std::wstring FullPipePath(const std::wstring& pipeName)
        {
            return L"\\\\.\\pipe\\" + pipeName;
        }
    }

    OverlayPipeClient::~OverlayPipeClient()
    {
        Stop();
    }

    void OverlayPipeClient::Start(const std::wstring& pipeName)
    {
        bool expected = false;
        if (!running_.compare_exchange_strong(expected, true))
            return; // already started

        pipeName_ = pipeName;
        stopEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);   // manual-reset
        outputEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr); // auto-reset: something to send
        readEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);   // manual-reset OVERLAPPED event
        writeEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);  // manual-reset OVERLAPPED event

        if (stopEvent_ == nullptr || outputEvent_ == nullptr || readEvent_ == nullptr || writeEvent_ == nullptr)
        {
            OverlayLog::Write("Pipe: could not create the events for the overlay pipe worker; not connecting.");
            running_.store(false);
            return;
        }

        workerThread_ = CreateThread(nullptr, 0, &WorkerThunk, this, 0, nullptr);
        if (workerThread_ == nullptr)
        {
            OverlayLog::Write("Pipe: could not create the overlay pipe worker thread; not connecting.");
            running_.store(false);
        }
    }

    unsigned long __stdcall OverlayPipeClient::WorkerThunk(void* self)
    {
        static_cast<OverlayPipeClient*>(self)->Worker();
        return 0;
    }

    void OverlayPipeClient::Stop()
    {
        if (!running_.exchange(false))
            return;

        if (stopEvent_ != nullptr)
            SetEvent(stopEvent_);

        if (workerThread_ != nullptr)
        {
            WaitForSingleObject(workerThread_, INFINITE);
            CloseHandle(workerThread_);
            workerThread_ = nullptr;
        }

        ClosePipe();

        if (stopEvent_ != nullptr) { CloseHandle(stopEvent_); stopEvent_ = nullptr; }
        if (outputEvent_ != nullptr) { CloseHandle(outputEvent_); outputEvent_ = nullptr; }
        if (readEvent_ != nullptr) { CloseHandle(readEvent_); readEvent_ = nullptr; }
        if (writeEvent_ != nullptr) { CloseHandle(writeEvent_); writeEvent_ = nullptr; }
    }

    void OverlayPipeClient::Worker()
    {
        while (running_.load())
        {
            if (!ConnectWithRetry())
                break; // stopped, or gave up waiting for the server

            connected_.store(true, std::memory_order_release);
            OverlayLog::Write("Pipe: connected to the overlay consumer on \\\\.\\pipe\\" + Narrow(pipeName_) + ".");

            RunSession();

            OnDisconnected();

            if (!running_.load())
                break;

            OverlayLog::Write("Pipe: the overlay consumer disconnected.");
        }

        connected_.store(false, std::memory_order_release);
    }

    bool OverlayPipeClient::ConnectWithRetry()
    {
        const std::wstring path = FullPipePath(pipeName_);
        const DWORD deadline = GetTickCount() + kConnectTimeoutMs;

        while (running_.load())
        {
            connectAttempts_.fetch_add(1);

            const HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                            OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
            if (pipe != INVALID_HANDLE_VALUE)
            {
                pipe_ = pipe; // only the worker does I/O on it, and Stop closes it after joining us
                return true;
            }

            if (GetLastError() == ERROR_PIPE_BUSY)
                WaitNamedPipeW(path.c_str(), kConnectRetryMs); // a server exists but has no free instance yet

            if (GetTickCount() >= deadline)
            {
                OverlayLog::Write("Pipe: no overlay consumer accepted a connection on \\\\.\\pipe\\" +
                                  Narrow(pipeName_) + " in time; the overlay will composite nothing.");
                return false;
            }

            if (WaitForSingleObject(stopEvent_, kConnectRetryMs) == WAIT_OBJECT_0)
                return false; // told to stop while waiting for the server
        }

        return false;
    }

    // The read+write session. A persistent overlapped read for inbound publishes runs alongside the
    // outbound sends: the worker waits on {stop, read-complete, something-to-send} and services whichever
    // fires. Reads and writes are independent directions of the duplex pipe, so an in-flight read stays
    // pending across a write and vice versa. Returns when the pipe breaks or Stop is signalled.
    void OverlayPipeClient::RunSession()
    {
        // Send anything already pending (a surface request set before the connection came up, and the
        // overlay state, which is resent on every reconnect).
        if (!DrainOutput())
            return;

        // Inbound framing: read a 16-byte header, then its payload, then dispatch and repeat.
        enum class Phase { Header, Payload };
        Phase phase = Phase::Header;
        uint8_t headerBuf[IpcFrameHeader::kSize];
        uint8_t payloadBuf[kMaxInboundPayload];
        IpcFrameHeader header{};
        uint32_t need = IpcFrameHeader::kSize;
        uint32_t got = 0;
        bool readPending = false;
        OVERLAPPED readOverlapped{};
        readOverlapped.hEvent = readEvent_;

        auto advance = [&](DWORD count) -> bool {
            got += count;
            if (got < need)
                return true; // more of this header/payload to read

            if (phase == Phase::Header)
            {
                if (!IpcFrameHeader::TryRead(headerBuf, header))
                {
                    OverlayLog::Write("Pipe: malformed frame header from the consumer; dropping the connection.");
                    return false;
                }

                if (header.payloadLength == 0)
                {
                    // A header-only frame: nothing the shim expects, but stay frame-aligned and continue.
                    got = 0;
                    return true;
                }

                if (header.payloadLength > kMaxInboundPayload)
                {
                    OverlayLog::Write("Pipe: consumer frame payload is larger than any overlay message; dropping the connection.");
                    return false;
                }

                phase = Phase::Payload;
                need = header.payloadLength;
                got = 0;
                return true;
            }

            // A whole payload is in payloadBuf.
            if (!DispatchInboundFrame(header, payloadBuf))
                return false;

            phase = Phase::Header;
            need = IpcFrameHeader::kSize;
            got = 0;
            return true;
        };

        for (;;)
        {
            if (!readPending)
            {
                uint8_t* destination = (phase == Phase::Header ? headerBuf : payloadBuf) + got;
                const DWORD toRead = need - got;

                ResetEvent(readEvent_);
                DWORD read = 0;
                const BOOL ok = ReadFile(pipe_, destination, toRead, &read, &readOverlapped);
                if (ok)
                {
                    if (read == 0 || !advance(read)) // 0 = end of stream
                        return;
                    continue;
                }
                if (GetLastError() != ERROR_IO_PENDING)
                    return; // broken pipe, or the consumer closed its end
                readPending = true;
            }

            HANDLE waits[3] = {stopEvent_, readEvent_, outputEvent_};
            const DWORD result = WaitForMultipleObjects(3, waits, FALSE, INFINITE);
            if (result == WAIT_OBJECT_0)
            {
                CancelIoEx(pipe_, &readOverlapped);
                return; // stopping
            }

            if (result == WAIT_OBJECT_0 + 1)
            {
                DWORD read = 0;
                if (!GetOverlappedResult(pipe_, &readOverlapped, &read, FALSE))
                    return;
                readPending = false;
                if (read == 0 || !advance(read))
                    return;
            }
            else if (result == WAIT_OBJECT_0 + 2)
            {
                if (!DrainOutput())
                    return; // a send failed; the read that follows would find the broken pipe anyway
            }
            else
            {
                return;
            }
        }
    }

    bool OverlayPipeClient::DispatchInboundFrame(const IpcFrameHeader& header, const uint8_t* payload)
    {
        if (header.messageKind == SrtOverlayMessage_SurfacePublish &&
            header.payloadLength == sizeof(SrtOverlaySurfacePublish))
        {
            SrtOverlaySurfacePublish publish{};
            std::memcpy(&publish, payload, sizeof(publish));
            StorePublish(publish);
            publishesReceived_.fetch_add(1);
        }
        // Unknown or wrong-sized messages are ignored: the framing kept us aligned, so a future message
        // the shim does not yet understand is harmless rather than fatal.
        return true;
    }

    bool OverlayPipeClient::DrainOutput()
    {
        // 1. The surface request, if a newer one is unsent.
        if (SendPendingRequest() == SendResult::Failed)
            return false;

        // 2. The overlay state, if it changed.
        SrtOverlayState state{};
        bool sendState = false;
        {
            std::lock_guard<std::mutex> lock(outputMutex_);
            if (stateDirty_)
            {
                state = state_;
                stateDirty_ = false;
                sendState = true;
            }
        }
        if (sendState && !WriteFrame(SrtOverlayMessage_OverlayState, &state, sizeof(state)))
            return false;

        // 3. Queued input, in batches of at most SRT_OVERLAY_MAX_INPUT_BATCH.
        for (;;)
        {
            SrtOverlayInputBatchHeader batchHeader{};
            SrtOverlayInputEvent events[SRT_OVERLAY_MAX_INPUT_BATCH];
            uint32_t count = 0;
            {
                std::lock_guard<std::mutex> lock(outputMutex_);
                if (inputCount_ == 0 && inputDropped_ == 0)
                    break;

                batchHeader.dropped = inputDropped_;
                inputDropped_ = 0;
                while (count < SRT_OVERLAY_MAX_INPUT_BATCH && inputCount_ > 0)
                {
                    events[count++] = inputRing_[inputHead_];
                    inputHead_ = (inputHead_ + 1) % kInputCapacity;
                    --inputCount_;
                }
                batchHeader.count = count;
            }

            uint8_t payload[sizeof(SrtOverlayInputBatchHeader) + SRT_OVERLAY_MAX_INPUT_BATCH * sizeof(SrtOverlayInputEvent)];
            std::memcpy(payload, &batchHeader, sizeof(batchHeader));
            if (count > 0)
                std::memcpy(payload + sizeof(batchHeader), events, count * sizeof(SrtOverlayInputEvent));

            const uint32_t size = static_cast<uint32_t>(sizeof(batchHeader) + count * sizeof(SrtOverlayInputEvent));
            if (!WriteFrame(SrtOverlayMessage_InputBatch, payload, size))
                return false;

            inputSent_.fetch_add(static_cast<long>(count));

            if (count < SRT_OVERLAY_MAX_INPUT_BATCH)
                break; // the queue is drained
        }

        return true;
    }

    OverlayPipeClient::SendResult OverlayPipeClient::SendPendingRequest()
    {
        uint64_t generation = 0;
        SrtOverlaySurfaceRequest local{};
        {
            std::lock_guard<std::mutex> lock(requestMutex_);
            if (!hasRequest_ || sentGen_ == requestGen_)
                return SendResult::Nothing;
            generation = requestGen_;
            local = request_;
        }

        if (!WriteFrame(SrtOverlayMessage_SurfaceRequest, &local, sizeof(local)))
        {
            OverlayLog::Write("Pipe: failed to send the surface request to the consumer.");
            return SendResult::Failed;
        }

        {
            std::lock_guard<std::mutex> lock(requestMutex_);
            if (sentGen_ < generation)
                sentGen_ = generation; // a newer request that arrived mid-write stays pending and resends
        }

        char line[160];
        std::snprintf(line, sizeof(line), "Pipe: sent surface request %ux%u, format %d, adapter 0x%08X%08X.",
                      local.width, local.height, local.format,
                      static_cast<unsigned>(local.adapterLuidHigh), local.adapterLuidLow);
        OverlayLog::Write(line);
        return SendResult::Sent;
    }

    void OverlayPipeClient::SetSurfaceRequest(const SrtOverlaySurfaceRequest& request)
    {
        {
            std::lock_guard<std::mutex> lock(requestMutex_);
            request_ = request;
            hasRequest_ = true;
            ++requestGen_; // marks the request unsent (sentGen_ now trails requestGen_)
        }
        SetEvent(outputEvent_); // wake the worker; no I/O happens on the caller's thread
    }

    void OverlayPipeClient::EnqueueInput(const SrtOverlayInputEvent& event)
    {
        {
            std::lock_guard<std::mutex> lock(outputMutex_);
            if (inputCount_ >= kInputCapacity)
            {
                // Full: drop the newest and mark it, so the consumer knows to resync held keys rather
                // than believing it saw every up/down.
                ++inputDropped_;
            }
            else
            {
                const int tail = (inputHead_ + inputCount_) % kInputCapacity;
                inputRing_[tail] = event;
                ++inputCount_;
            }
        }
        SetEvent(outputEvent_);
    }

    void OverlayPipeClient::SetOverlayState(const SrtOverlayState& state)
    {
        {
            std::lock_guard<std::mutex> lock(outputMutex_);
            state_ = state;
            hasState_ = true;
            stateDirty_ = true;
        }
        SetEvent(outputEvent_);
    }

    bool OverlayPipeClient::WriteFrame(uint16_t kind, const void* payload, uint32_t size)
    {
        if (pipe_ == INVALID_HANDLE_VALUE)
            return false;

        IpcFrameHeader header;
        header.channel = IpcChannel::Control;
        header.codec = IpcCodec::Blittable;
        header.messageKind = kind;
        header.correlationId = 0;
        header.payloadLength = size;

        // A frame is a 16-byte header plus one packed message; the largest is an input batch
        // (8 + 64 * 24 = 1544 bytes). A stack buffer holds the header and the payload together so the
        // whole frame goes out in one WriteFile stream.
        uint8_t buffer[IpcFrameHeader::kSize + sizeof(SrtOverlayInputBatchHeader) +
                       SRT_OVERLAY_MAX_INPUT_BATCH * sizeof(SrtOverlayInputEvent)];
        if (size > sizeof(buffer) - IpcFrameHeader::kSize)
            return false;

        header.Write(buffer);
        if (size > 0 && payload != nullptr)
            std::memcpy(buffer + IpcFrameHeader::kSize, payload, size);

        const uint32_t total = IpcFrameHeader::kSize + size;
        uint32_t written = 0;
        while (written < total)
        {
            OVERLAPPED overlapped{};
            overlapped.hEvent = writeEvent_;
            ResetEvent(writeEvent_);

            DWORD wrote = 0;
            const BOOL ok = WriteFile(pipe_, buffer + written, total - written, &wrote, &overlapped);
            if (!ok)
            {
                if (GetLastError() != ERROR_IO_PENDING)
                    return false;

                HANDLE waits[2] = {stopEvent_, writeEvent_};
                const DWORD result = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
                if (result == WAIT_OBJECT_0)
                {
                    CancelIoEx(pipe_, &overlapped);
                    return false; // stopping
                }
                if (!GetOverlappedResult(pipe_, &overlapped, &wrote, FALSE))
                    return false;
            }

            if (wrote == 0)
                return false;
            written += wrote;
        }

        return true;
    }

    void OverlayPipeClient::StorePublish(const SrtOverlaySurfacePublish& publish)
    {
        // Seqlock write: bump to odd, publish the bytes, bump to even. A render-thread reader that sees
        // an odd count, or a count that changed across the copy, retries.
        const uint32_t seq = publishSeq_.load(std::memory_order_relaxed);
        publishSeq_.store(seq + 1, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);

        publish_ = publish;

        std::atomic_thread_fence(std::memory_order_release);
        publishSeq_.store(seq + 2, std::memory_order_relaxed);
        hasPublish_.store(true, std::memory_order_release);
    }

    bool OverlayPipeClient::TryGetLatestPublish(SrtOverlaySurfacePublish& out) const
    {
        if (!hasPublish_.load(std::memory_order_acquire))
            return false;

        for (int tries = 0; tries < kSeqlockReadTries; ++tries)
        {
            const uint32_t before = publishSeq_.load(std::memory_order_acquire);
            if (before & 1u)
                continue; // writer mid-update

            std::atomic_thread_fence(std::memory_order_acquire);
            const SrtOverlaySurfacePublish snapshot = publish_;
            std::atomic_thread_fence(std::memory_order_acquire);

            if (publishSeq_.load(std::memory_order_acquire) == before)
            {
                if (snapshot.alive == 0)
                    return false;
                out = snapshot;
                return true;
            }
        }

        return false; // too contended to get a stable read; skip compositing this frame
    }

    void OverlayPipeClient::OnDisconnected()
    {
        connected_.store(false, std::memory_order_release);
        hasPublish_.store(false, std::memory_order_release); // consumer gone; stop compositing
        ClosePipe();

        // Resend the surface request and the state on the next connection, and discard queued input
        // (it belonged to a consumer that is gone).
        {
            std::lock_guard<std::mutex> lock(requestMutex_);
            sentGen_ = 0; // request (if any) is now unsent again
        }
        {
            std::lock_guard<std::mutex> lock(outputMutex_);
            inputHead_ = 0;
            inputCount_ = 0;
            inputDropped_ = 0;
            stateDirty_ = hasState_; // resend the last state to a fresh consumer
        }
    }

    void OverlayPipeClient::ClosePipe()
    {
        if (pipe_ != INVALID_HANDLE_VALUE)
        {
            CloseHandle(pipe_);
            pipe_ = INVALID_HANDLE_VALUE;
        }
    }

    std::string OverlayPipeClient::DescribeActivity() const
    {
        char buffer[256];
        if (connected_.load())
        {
            SrtOverlaySurfacePublish latest{};
            const bool have = TryGetLatestPublish(latest);
            std::snprintf(buffer, sizeof(buffer), "pipe connected, publishes %ld, input sent %ld, latest %s",
                          publishesReceived_.load(), inputSent_.load(), have ? "index/fence live" : "none yet");
        }
        else
        {
            std::snprintf(buffer, sizeof(buffer), "pipe not connected (attempts %ld)", connectAttempts_.load());
        }
        return buffer;
    }
}
