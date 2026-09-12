// The shim's log file. One writer, opened once, shared for reading so it can be tailed live.
//
// This is the only diagnostic channel the injected side has until the pipe to the runner is up (step
// 5): the shim runs inside a process nothing can attach a debugger to without stopping the game, and
// a fault here is a fault in someone's play session.
//
// NOTHING on the render path may call this. It formats, takes a lock and writes a file, and the
// per-frame budget is under a millisecond on the game's own thread. Startup, teardown, hook
// installation and error paths only - exactly as the C# spec's OverlayLog restricts itself.

#ifndef SRTOVERLAY_LOG_H
#define SRTOVERLAY_LOG_H

#include <string>
#include <string_view>

namespace srtoverlay::core
{
    class OverlayLog
    {
    public:
        // Open path for writing, replacing any previous file. Shared for read+write so the log can be
        // tailed while the game runs and a second injection does not fail on a lock. Failure to open is
        // swallowed: an overlay that refuses to start because it could not write a log is worse than
        // one that runs silently.
        static void Open(const std::wstring& path);

        // Whether a file is open. False is normal - an empty log path disables logging.
        static bool IsOpen();

        // Write one line, timestamped (UTC) and stamped with the calling thread id.
        static void Write(std::string_view message);

        // Convenience: format an HRESULT-style failure. Kept allocation-heavy and off the render path.
        static void WriteHResult(std::string_view context, long hr);

        // Flush and close. Safe to call when nothing is open.
        static void Close();
    };
}

#endif // SRTOVERLAY_LOG_H
