#include "OverlayLog.h"

#include <windows.h>

#include <cstdio>
#include <mutex>

namespace srtoverlay::core
{
    namespace
    {
        std::mutex g_gate;
        HANDLE g_file = INVALID_HANDLE_VALUE;

        // Everything but the directory of a full path, created if missing, so a log path pointing into
        // a folder that does not exist yet still opens rather than silently failing.
        void EnsureDirectory(const std::wstring& path)
        {
            const size_t slash = path.find_last_of(L"\\/");
            if (slash == std::wstring::npos)
                return;

            const std::wstring directory = path.substr(0, slash);
            if (!directory.empty())
                CreateDirectoryW(directory.c_str(), nullptr);
        }

        void WriteRaw(std::string_view text)
        {
            if (g_file == INVALID_HANDLE_VALUE)
                return;

            DWORD written = 0;
            WriteFile(g_file, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
        }

        // Close with g_gate already held. std::mutex is not recursive, so the public Close and Open
        // both take the lock and call this rather than re-entering Close.
        void CloseLocked()
        {
            if (g_file != INVALID_HANDLE_VALUE)
            {
                FlushFileBuffers(g_file);
                CloseHandle(g_file);
                g_file = INVALID_HANDLE_VALUE;
            }
        }
    }

    void OverlayLog::Open(const std::wstring& path)
    {
        std::scoped_lock lock(g_gate);
        CloseLocked();

        if (path.empty())
            return;

        EnsureDirectory(path);

        // FILE_SHARE_READ | FILE_SHARE_WRITE so the log can be tailed live and a second injection into
        // the same process does not fail on a lock it cannot see. CREATE_ALWAYS replaces any previous
        // file, matching the C# spec's FileMode.Create.
        g_file = CreateFileW(
            path.c_str(),
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (g_file == INVALID_HANDLE_VALUE)
        {
            // Nowhere to report it to; the log is the reporting channel.
            return;
        }
    }

    bool OverlayLog::IsOpen()
    {
        std::scoped_lock lock(g_gate);
        return g_file != INVALID_HANDLE_VALUE;
    }

    void OverlayLog::Write(std::string_view message)
    {
        std::scoped_lock lock(g_gate);
        if (g_file == INVALID_HANDLE_VALUE)
            return;

        SYSTEMTIME now{};
        GetSystemTime(&now);

        char prefix[64];
        const int prefixLen = std::snprintf(
            prefix, sizeof(prefix),
            "%04u-%02u-%02u %02u:%02u:%02u.%03u [t%lu] ",
            now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds,
            GetCurrentThreadId());

        if (prefixLen > 0)
            WriteRaw(std::string_view(prefix, static_cast<size_t>(prefixLen)));

        WriteRaw(message);
        WriteRaw(std::string_view("\r\n", 2));

        FlushFileBuffers(g_file);
    }

    void OverlayLog::WriteHResult(std::string_view context, long hr)
    {
        char line[256];
        const int len = std::snprintf(line, sizeof(line), "%.*s failed, 0x%08lX.",
                                      static_cast<int>(context.size()), context.data(),
                                      static_cast<unsigned long>(hr));
        if (len > 0)
            Write(std::string_view(line, static_cast<size_t>(len)));
    }

    void OverlayLog::Close()
    {
        std::scoped_lock lock(g_gate);
        CloseLocked();
    }
}
