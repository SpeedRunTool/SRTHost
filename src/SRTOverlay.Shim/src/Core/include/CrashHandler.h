// A last-chance crash handler for the injected shim: when an unhandled exception reaches the top of a
// game thread, write a minidump and a text crash report so a fault the shim may have caused (or merely
// witnessed) leaves an artifact instead of a silent game crash.
//
// Adapted from SpeedrunTooling/SRTPluginProviderRE9's src/SRTPluginRE9/src/Hook/CrashHandler.cpp - the
// SEH discipline, the parked worker thread, the allocation-free report building and the bounded memory
// windows are all lifted from it. Trimmed to the shim: no game-specific state, no logger ring; what it
// keeps that matters here is the hook inventory, because the shim's whole job is vtable hooks and a
// crash on the render path is the failure mode this exists to explain.
//
// It observes and dumps, then returns EXCEPTION_CONTINUE_SEARCH so REFramework and WER still run.

#ifndef SRTOVERLAY_CRASH_HANDLER_H
#define SRTOVERLAY_CRASH_HANDLER_H

#include <windows.h>

#include <atomic>
#include <cstdint>

namespace srtoverlay::core::CrashHandler
{
    // Identity of a PE image, read from its IMAGE_DIRECTORY_ENTRY_DEBUG RSDS record - what a debugger
    // matches a .pdb against.
    struct BuildIdentity
    {
        GUID PdbGuid{};
        uint32_t PdbAge = 0;
        uint32_t LinkTimestamp = 0;
        uintptr_t ImageBase = 0;
        uint32_t SizeOfImage = 0;
        char PdbPath[MAX_PATH]{};
        bool Valid = false;
    };

    inline constexpr uint32_t MaxHookRecords = 12;

    // Installs the unhandled-exception filter and parks the crash worker thread. Call once, from the
    // shim's start path (not under the loader lock). Dumps and reports are written beside logPath (its
    // directory), and a one-line note is appended to logPath itself; if logPath is null or empty, the
    // shim module's own directory is used and no note is written.
    void Install(HMODULE shimModule, const wchar_t* logPath) noexcept;

    // Restores the previous unhandled-exception filter and stops the parked worker thread. MUST be
    // called before the shim unloads. The RE9 reference also FreeLibraryAndExitThread's itself, but it
    // never tears its crash handler down - it leaves SetUnhandledExceptionFilter pointing at
    // soon-to-be-unmapped code and the worker parked, which it gets away with only because unload is
    // rare there and nothing signals the worker again. This shim unloads and reinjects as a core
    // workflow, so it undoes both cleanly here. Safe to call when Install was never called or already
    // undone.
    void Uninstall() noexcept;

    // Records a hook target so the crash report can name it and its owning module. Call as each target
    // address is resolved, before the detour is attempted. Name is a short label, e.g. "Present".
    void RecordHook(const char* name, void* target) noexcept;

    // Updates a previously recorded hook with whether its detour actually took.
    void MarkHookInstalled(const char* name, bool installed) noexcept;

    // Reserves stack for the calling thread so a stack-overflow crash still leaves room for the filter.
    void ReserveStackForCrashHandling() noexcept;
}

#endif // SRTOVERLAY_CRASH_HANDLER_H
