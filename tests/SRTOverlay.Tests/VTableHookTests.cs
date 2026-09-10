using System.Runtime.InteropServices;
using SRTOverlay.Core;

namespace SRTOverlay.Tests;

/// <summary>
/// The vtable hook, exercised against a synthetic vtable rather than a graphics driver.
/// </summary>
/// <remarks>
/// <para>
/// A vtable is just an array of function pointers, so the mechanics - make the page writable, swap
/// one slot atomically, put it back, refuse when someone else got there first - can be pinned
/// exactly without D3D12, without a game, and without a render thread. What cannot be tested here is
/// the question that actually decided the design: whether the game's swapchain points at the same
/// vtable array our throwaway object does. That is settled by injecting and watching the counter,
/// which is what <c>SRTOverlay.Spike</c> is for.
/// </para>
/// <para>
/// The slots hold ordinary addresses rather than real code, because nothing here calls through them.
/// </para>
/// </remarks>
public sealed unsafe class VTableHookTests : IDisposable
{
    private const int SlotCount = 16;
    private const int Slot = 8;

    private readonly nint* vtable = (nint*)NativeMemory.AllocZeroed(SlotCount, (nuint)sizeof(nint));

    public VTableHookTests()
    {
        // Distinct, non-zero, obviously-fake addresses so a wrong slot is unmistakable.
        for (int i = 0; i < SlotCount; i++)
            vtable[i] = 0x1000 + i;
    }

    public void Dispose() => NativeMemory.Free(vtable);

    private static nint Detour => 0xDEAD;

    [Fact]
    public void InstallRedirectsExactlyOneSlotAndRemembersTheOriginal()
    {
        nint originalBefore = vtable[Slot];

        VTableHook? hook = VTableHook.Install("Present", (nint)vtable, Slot, Detour);

        Assert.NotNull(hook);
        Assert.Equal(originalBefore, hook!.Original);
        Assert.Equal(Detour, vtable[Slot]);
        Assert.True(hook.IsInstalled);

        // Neighbours untouched: an off-by-one in slot arithmetic would call the wrong function
        // through a pointer, which is a crash in a game rather than a failing build.
        Assert.Equal(0x1000 + Slot - 1, vtable[Slot - 1]);
        Assert.Equal(0x1000 + Slot + 1, vtable[Slot + 1]);
    }

    [Fact]
    public void UninstallPutsTheOriginalBack()
    {
        VTableHook hook = VTableHook.Install("Present", (nint)vtable, Slot, Detour)!;

        Assert.True(hook.Uninstall());
        Assert.Equal(0x1000 + Slot, vtable[Slot]);
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public void UninstallRefusesWhenSomethingElseHookedTheSlotAfterUs()
    {
        VTableHook hook = VTableHook.Install("Present", (nint)vtable, Slot, Detour)!;

        // Another tool hooks the same slot after we did.
        vtable[Slot] = 0xBEEF;

        // Restoring would tear out their hook and point the slot at a function they no longer
        // expect. Leaving it alone is the lesser harm, and the caller logs that it happened.
        Assert.False(hook.Uninstall());
        Assert.Equal(0xBEEF, vtable[Slot]);
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public void IsInstalledGoesFalseWhenTheSlotIsReplaced()
    {
        VTableHook hook = VTableHook.Install("Present", (nint)vtable, Slot, Detour)!;
        Assert.True(hook.IsInstalled);

        vtable[Slot] = 0xBEEF;

        // The symptom of another tool hooking over us would otherwise be the overlay silently
        // doing nothing at all.
        Assert.False(hook.IsInstalled);
    }

    [Theory]
    [InlineData(true, false)]  // null vtable
    [InlineData(false, true)]  // null detour
    public void InstallRefusesNullArguments(bool nullVTable, bool nullDetour)
        => Assert.Null(VTableHook.Install("Present", nullVTable ? 0 : (nint)vtable, Slot, nullDetour ? 0 : Detour));

    [Fact]
    public void InstallRefusesToHookASlotThatAlreadyHoldsTheDetour()
    {
        // Double-installing would capture our own detour as "the original" and build a loop that
        // recurses until the render thread's stack runs out.
        vtable[Slot] = Detour;

        Assert.Null(VTableHook.Install("Present", (nint)vtable, Slot, Detour));
    }
}
