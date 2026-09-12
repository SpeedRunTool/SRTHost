#include "VTableHook.h"

namespace srtoverlay::core
{
    bool VTableHook::Install(std::string name, void** vtable, int slot, void* detour)
    {
        if (vtable == nullptr || detour == nullptr)
            return false;

        void** slotAddress = vtable + slot;
        void* original = *slotAddress;

        if (original == nullptr || original == detour)
            return false;

        DWORD previous = 0;
        if (!VirtualProtect(slotAddress, sizeof(void*), PAGE_READWRITE, &previous))
            return false;

        // Interlocked, because the game may be calling through this slot on its render thread at the
        // moment it is replaced. A torn pointer write there is a jump to a spliced address - an
        // immediate crash in someone's game; an aligned atomic exchange cannot tear.
        InterlockedExchangePointer(slotAddress, detour);

        VirtualProtect(slotAddress, sizeof(void*), previous, &previous);

        // Confirm rather than trust. A page can be write-protected in ways VirtualProtect reports
        // success for, and a hook that silently did not take is worse than one that failed loudly.
        if (*slotAddress != detour)
            return false;

        name_ = std::move(name);
        slotAddress_ = slotAddress;
        original_ = original;
        detour_ = detour;
        return true;
    }

    bool VTableHook::Uninstall()
    {
        if (slotAddress_ == nullptr)
            return false;

        if (*slotAddress_ != detour_)
        {
            // Something else hooked the slot after us; leave it. Forget our own state either way so a
            // second Uninstall is a no-op.
            slotAddress_ = nullptr;
            return false;
        }

        DWORD previous = 0;
        if (!VirtualProtect(slotAddress_, sizeof(void*), PAGE_READWRITE, &previous))
            return false;

        InterlockedExchangePointer(slotAddress_, original_);
        VirtualProtect(slotAddress_, sizeof(void*), previous, &previous);

        slotAddress_ = nullptr;
        return true;
    }

    bool VTableHook::IsInstalled() const
    {
        return slotAddress_ != nullptr && *slotAddress_ == detour_;
    }
}
