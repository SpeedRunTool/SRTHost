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
        requestEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr); // auto-reset
        readEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);   // manual-reset OVERLAPPED event
        writeEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);  // manual-reset OVERLAPPED event

        if (stopEvent_ == nullptr || requestEvent_ == nullptr || readEvent_ == nullptr || writeEvent_ == nullptr)
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
        if (requestEvent_ != nullptr) { CloseHandle(requestEvent_); requestEvent_ = nullptr; }
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

            // Nothing is published until the consumer has the surface request, so send it first (it is
            // set by the compositor a few frames into the game), then read publishes until the consumer
            // goes away. A resize sets a newer request, sent between frames in the read loop.
            if (WaitAndSendFirstRequest())
            {
                for (;;)
                {
                    TrySendPendingRequest();

                    uint8_t header[IpcFrameHeader::kSize];
                    if (!ReadExact(header, IpcFrameHeader::kSize))
                        break;

                    IpcFrameHeader frame;
                    if (!IpcFrameHeader::TryRead(header, frame))
                    {
                        OverlayLog::Write("Pipe: malformed frame header from the consumer; dropping the connection.");
                        break;
                    }

                    bool payloadOk = true;
                    if (frame.payloadLength == sizeof(SrtOverlaySurfacePublish) &&
                        frame.messageKind == SrtOverlayMessage_SurfacePublish)
                    {
                        SrtOverlaySurfacePublish publish{};
                        payloadOk = ReadExact(reinterpret_cast<uint8_t*>(&publish), frame.payloadLength);
                        if (payloadOk)
                        {
                            StorePublish(publish);
                            publishesReceived_.fetch_add(1);
                        }
                    }
                    else if (frame.payloadLength > 0)
                    {
                        // Unknown or wrong-sized message: read and discard its bytes to stay frame-aligned.
                        uint8_t scratch[256];
                        uint32_t remaining = frame.payloadLength;
                        while (remaining > 0 && payloadOk)
                        {
                            const uint32_t chunk = remaining < sizeof(scratch) ? remaining : static_cast<uint32_t>(sizeof(scratch));
                            payloadOk = ReadExact(scratch, chunk);
                            remaining -= chunk;
                        }
                    }

                    if (!payloadOk)
                        break;
                }
            }

            connected_.store(false, std::memory_order_release);
            hasPublish_.store(false, std::memory_order_release); // consumer gone; stop compositing
            ClosePipe();

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

    OverlayPipeClient::SendResult OverlayPipeClient::SendPendingIfAny()
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

    bool OverlayPipeClient::WaitAndSendFirstRequest()
    {
        for (;;)
        {
            switch (SendPendingIfAny())
            {
                case SendResult::Sent:
                    return true;
                case SendResult::Failed:
                    return false;
                case SendResult::Nothing:
                    break; // no request yet; wait for the compositor to set one
            }

            HANDLE waits[2] = {stopEvent_, requestEvent_};
            if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_OBJECT_0)
                return false; // stopping
        }
    }

    void OverlayPipeClient::TrySendPendingRequest()
    {
        // Best-effort between frames; a failure is caught by the read that follows.
        SendPendingIfAny();
    }

    void OverlayPipeClient::SetSurfaceRequest(const SrtOverlaySurfaceRequest& request)
    {
        {
            std::lock_guard<std::mutex> lock(requestMutex_);
            request_ = request;
            hasRequest_ = true;
            ++requestGen_; // marks the request unsent (sentGen_ now trails requestGen_)
        }
        SetEvent(requestEvent_); // wake the worker; no I/O happens on the caller's thread
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

        // One frame is a 16-byte header plus a small packed struct; a stack buffer holds both.
        uint8_t buffer[IpcFrameHeader::kSize + 128];
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

    bool OverlayPipeClient::ReadExact(uint8_t* buffer, uint32_t length)
    {
        uint32_t got = 0;
        while (got < length && running_.load())
        {
            OVERLAPPED overlapped{};
            overlapped.hEvent = readEvent_;

            DWORD read = 0;
            const BOOL ok = ReadFile(pipe_, buffer + got, length - got, &read, &overlapped);
            if (!ok)
            {
                if (GetLastError() != ERROR_IO_PENDING)
                    return false; // broken pipe, or the consumer closed its end

                HANDLE waits[2] = {stopEvent_, readEvent_};
                const DWORD result = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
                if (result == WAIT_OBJECT_0)
                {
                    CancelIoEx(pipe_, &overlapped);
                    return false; // stopping
                }
                if (!GetOverlappedResult(pipe_, &overlapped, &read, FALSE))
                    return false;
            }

            if (read == 0)
                return false; // end of stream
            got += read;
        }

        return got == length;
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
            std::snprintf(buffer, sizeof(buffer), "pipe connected, publishes %ld, latest %s",
                          publishesReceived_.load(), have ? "index/fence live" : "none yet");
        }
        else
        {
            std::snprintf(buffer, sizeof(buffer), "pipe not connected (attempts %ld)", connectAttempts_.load());
        }
        return buffer;
    }
}
