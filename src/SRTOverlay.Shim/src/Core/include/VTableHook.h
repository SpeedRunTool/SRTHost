// Redirects one COM vtable slot to a detour, and puts it back on request.
//
// This is the hooking technique section 11 commits to, and the reason is reputation rather than
// elegance. An inline hook makes someone else's code writable, overwrites its first instructions with
// a jump, and allocates an EXECUTABLE page for the relocated trampoline - the RWX allocation plus
// foreign code modification that is the canonical heuristic signature. A vtable slot lives in a DATA
// page of dxgi.dll or d3d12.dll: hooking one is a VirtualProtect on data and a single pointer write,
// with no executable allocation and no instruction rewriting anywhere. Step 2 measured that this
// holds in re9.exe and coexists with REFramework.
//
// The C++ shim removes the NativeAOT runtime's own 1 MiB RWX bootstrap reservation the C# shim
// carried, so the "allocates no executable page" claim now holds cleanly rather than in the narrower
// measured form - see the plan's section 11 language note, reason 4.

#ifndef SRTOVERLAY_VTABLE_HOOK_H
#define SRTOVERLAY_VTABLE_HOOK_H

#include <windows.h>

#include <string>

namespace srtoverlay::core
{
    class VTableHook
    {
    public:
        VTableHook() = default;

        // Point slot `slot` of `vtable` at `detour`. On success the hook holds the original function
        // (which every detour must end up calling) and reports IsInstalled; on failure it is left
        // Empty(). A vtable is per-implementation, so the addresses read from a throwaway object are
        // the ones the game's object uses too.
        bool Install(std::string name, void** vtable, int slot, void* detour);

        // Put the original back, if our detour is still the one installed. Refuses when something else
        // hooked the slot after us - restoring then would tear out the other tool's hook and point the
        // slot at a function it no longer expects. Leaving a foreign hook in place is the lesser harm.
        bool Uninstall();

        // Whether the slot currently still holds our detour. Worth checking rather than assuming:
        // another tool hooking the same slot replaces the pointer, and the first symptom would
        // otherwise be the overlay silently doing nothing.
        bool IsInstalled() const;

        bool Empty() const { return slotAddress_ == nullptr; }
        const std::string& Name() const { return name_; }

        // The function the slot held before, which every detour must end up calling.
        void* Original() const { return original_; }

    private:
        std::string name_;
        void** slotAddress_ = nullptr;
        void* original_ = nullptr;
        void* detour_ = nullptr;
    };
}

#endif // SRTOVERLAY_VTABLE_HOOK_H
