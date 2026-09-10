using System.Runtime.InteropServices;

namespace SRTOverlay.Core;

/// <summary>
/// Redirects one COM vtable slot to a detour, and puts it back on request.
/// </summary>
/// <remarks>
/// <para>
/// This is the hooking technique section 11 commits to trying first, and the reason is reputation
/// rather than elegance. An inline hook makes someone else's code writable, overwrites its first
/// instructions with a jump, and allocates an <b>executable</b> page for the relocated trampoline -
/// the RWX allocation plus foreign code modification that is the canonical heuristic signature. A
/// vtable slot lives in a <i>data</i> page of <c>dxgi.dll</c> or <c>d3d12.dll</c>: hooking one is a
/// <c>VirtualProtect</c> on data and a single pointer write, with no executable allocation and no
/// instruction rewriting anywhere.
/// </para>
/// <para>
/// The tradeoff is real and the spike exists to measure it. An inline hook survives another tool
/// replacing the vtable pointer, or the game caching a raw function address and calling it directly;
/// a vtable hook does not. SafetyHook exists because vtable hooking has failure modes, and if this
/// one does not hold in practice the fallback is inline hooking - at which point the RWX allocation
/// is a considered cost rather than a default.
/// </para>
/// <para>
/// <b>There is a second, sharper failure mode specific to resolving vtables from a throwaway COM
/// object.</b> Reading slot addresses from an object we created ourselves gives the right
/// <i>function</i> addresses, because a vtable is per-implementation rather than per-object - which
/// is all an inline hook needs. But this class patches one specific vtable <i>array</i>, and if the
/// game's swapchain happens to point at a different array holding the same function pointers, the
/// patch is invisible to it. That cannot be reasoned about from outside; it is settled by installing
/// the hook and seeing whether the detour is called.
/// </para>
/// </remarks>
public sealed class VTableHook
{
    private readonly nint slotAddress;

    private VTableHook(string name, nint slotAddress, nint original, nint detour)
    {
        Name = name;
        this.slotAddress = slotAddress;
        Original = original;
        Detour = detour;
    }

    /// <summary>What was hooked, for the log.</summary>
    public string Name { get; }

    /// <summary>The function the slot held before, which every detour must end up calling.</summary>
    public nint Original { get; }

    /// <summary>The function the slot holds now.</summary>
    public nint Detour { get; }

    /// <summary>Whether the slot currently still holds our detour.</summary>
    /// <remarks>
    /// Worth checking rather than assuming: another tool hooking the same slot after us replaces the
    /// pointer, and the first symptom would otherwise be the overlay silently doing nothing.
    /// </remarks>
    public unsafe bool IsInstalled => *(nint*)slotAddress == Detour;

    /// <summary>
    /// Point slot <paramref name="slot"/> of <paramref name="vtable"/> at <paramref name="detour"/>.
    /// </summary>
    /// <returns>The hook, or <see langword="null"/> if the slot could not be written.</returns>
    public static unsafe VTableHook? Install(string name, nint vtable, int slot, nint detour)
    {
        if (vtable == 0 || detour == 0)
            return null;

        nint slotAddress = vtable + (slot * sizeof(nint));
        nint original = *(nint*)slotAddress;

        if (original == 0 || original == detour)
            return null;

        if (!NativeMethods.VirtualProtect(slotAddress, (nuint)sizeof(nint), NativeMethods.PAGE_READWRITE, out uint previous))
            return null;

        // Interlocked, because the game may be calling through this slot on its render thread at the
        // moment it is replaced. A torn pointer write there is a jump to a spliced address, which is
        // an immediate crash in someone's game; an aligned atomic exchange cannot tear.
        Interlocked.Exchange(ref *(nint*)slotAddress, detour);

        NativeMethods.VirtualProtect(slotAddress, (nuint)sizeof(nint), previous, out _);

        // Confirm rather than trust. A page can be write-protected in ways VirtualProtect reports
        // success for, and a hook that silently did not take is worse than one that failed loudly.
        return *(nint*)slotAddress == detour
            ? new VTableHook(name, slotAddress, original, detour)
            : null;
    }

    /// <summary>Put the original function back, if our detour is still the one installed.</summary>
    /// <remarks>
    /// Refuses when something else hooked the slot after us: restoring then would tear out the other
    /// tool's hook and point the slot at a function it no longer expects. Leaving a foreign hook in
    /// place is the lesser harm, and the log says so.
    /// </remarks>
    public unsafe bool Uninstall()
    {
        if (*(nint*)slotAddress != Detour)
            return false;

        if (!NativeMethods.VirtualProtect(slotAddress, (nuint)sizeof(nint), NativeMethods.PAGE_READWRITE, out uint previous))
            return false;

        Interlocked.Exchange(ref *(nint*)slotAddress, Original);
        NativeMethods.VirtualProtect(slotAddress, (nuint)sizeof(nint), previous, out _);

        return true;
    }
}
