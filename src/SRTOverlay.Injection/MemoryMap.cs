using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SRTOverlay.Injection;

/// <summary>
/// Walks a process's committed memory and reports the regions that are both writable and executable.
/// </summary>
/// <remarks>
/// <para>
/// This is a gate, not a diagnostic. Section 11 of the 5.0 plan commits the shim to never allocating
/// an executable page in the game's process - vtable hooks write to a data page and NativeAOT emits
/// no code at run time - and that claim is only worth making if it is measured. Spike step 2 asks for
/// exactly this: a memory-map dump showing the process gained no <c>PAGE_EXECUTE_READWRITE</c> region.
/// </para>
/// <para>
/// Compare a count taken before injecting with one taken after. An absolute count is meaningless -
/// games and their own middleware allocate RWX freely, and one has a JIT of its own - so the only
/// question this can answer is whether <i>we</i> added any.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class MemoryMap
{
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_EXECUTE = 0x10;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    /// <summary>One writable-and-executable region.</summary>
    /// <param name="BaseAddress">Where it starts.</param>
    /// <param name="Size">How large it is, in bytes.</param>
    /// <param name="Protection">The raw page protection constant.</param>
    /// <param name="AllocationBase">The reservation this region is part of.</param>
    /// <param name="Type"><c>MEM_IMAGE</c>, <c>MEM_MAPPED</c> or <c>MEM_PRIVATE</c>.</param>
    /// <remarks>
    /// <see cref="AllocationBase"/> and <see cref="Type"/> are carried because when a region does turn
    /// up, the first question is which of the three it is: an <c>IMAGE</c> region is a module's own
    /// writable-executable section and nothing allocated it, while a <c>PRIVATE</c> one was allocated
    /// by somebody, and that somebody has to be identified rather than assumed.
    /// </remarks>
    public readonly record struct ExecutableRegion(nint BaseAddress, nuint Size, uint Protection, nint AllocationBase, uint Type)
    {
        /// <summary>A line for a log or a console.</summary>
        public string Describe()
            => $"0x{BaseAddress:X16} {Size,12:N0} bytes {ProtectionName(Protection),-22} " +
               $"{TypeName(Type),-11} alloc 0x{AllocationBase:X16}";
    }

    /// <summary>Every committed region in <paramref name="processId"/> that is writable and executable.</summary>
    public static List<ExecutableRegion> FindWritableExecutableRegions(int processId)
        => FindRegions(processId, writableOnly: true);

    /// <summary>
    /// Every committed executable region, whether or not it is also writable.
    /// </summary>
    /// <remarks>
    /// The wider question, and it has to be asked separately or the narrower one misleads. A runtime
    /// that allocates its code heap as <c>PAGE_EXECUTE_READ</c> is invisible to
    /// <see cref="FindWritableExecutableRegions"/> while still having allocated executable memory -
    /// so "no RWX region appeared" can mean either "nothing was allocated" or "something was
    /// allocated with a protection nobody objects to", and those are very different answers.
    /// </remarks>
    public static List<ExecutableRegion> FindExecutableRegions(int processId)
        => FindRegions(processId, writableOnly: false);

    private static List<ExecutableRegion> FindRegions(int processId, bool writableOnly)
    {
        nint process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, (uint)processId);

        if (process == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess failed for pid {processId}.");

        try
        {
            List<ExecutableRegion> regions = [];
            nint address = 0;

            while (VirtualQueryEx(process, address, out MemoryBasicInformation information, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
            {
                bool interesting = information.State == MEM_COMMIT
                    && (writableOnly ? IsWritableExecutable(information.Protect) : IsExecutable(information.Protect));

                if (interesting)
                {
                    regions.Add(new ExecutableRegion(
                        information.BaseAddress,
                        information.RegionSize,
                        information.Protect,
                        information.AllocationBase,
                        information.Type));
                }

                nint next = information.BaseAddress + (nint)information.RegionSize;

                // A region that does not advance would loop forever, and the address space ends by
                // wrapping rather than by returning zero on some layouts.
                if (next <= address)
                    break;

                address = next;
            }

            return regions;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// Whether a page protection permits both writing and execution.
    /// </summary>
    /// <remarks>
    /// <c>WRITECOPY</c> counts. It is writable in every sense that matters here - the first write
    /// makes a private copy and the page stays executable - and a trampoline allocated that way would
    /// be exactly as visible to a scanner as one allocated <c>READWRITE</c>.
    /// </remarks>
    private static bool IsWritableExecutable(uint protection)
        => protection is PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY;

    /// <summary>Whether a page protection permits execution at all.</summary>
    private static bool IsExecutable(uint protection)
        => protection is PAGE_EXECUTE or PAGE_EXECUTE_READ or PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY;

    private const uint MEM_IMAGE = 0x1000000;
    private const uint MEM_MAPPED = 0x40000;
    private const uint MEM_PRIVATE = 0x20000;

    private static string TypeName(uint type) => type switch
    {
        MEM_IMAGE => "MEM_IMAGE",
        MEM_MAPPED => "MEM_MAPPED",
        MEM_PRIVATE => "MEM_PRIVATE",
        _ => $"0x{type:X}",
    };

    private static string ProtectionName(uint protection) => protection switch
    {
        PAGE_EXECUTE => "PAGE_EXECUTE",
        PAGE_EXECUTE_READ => "PAGE_EXECUTE_READ",
        PAGE_EXECUTE_READWRITE => "PAGE_EXECUTE_READWRITE",
        PAGE_EXECUTE_WRITECOPY => "PAGE_EXECUTE_WRITECOPY",
        _ => $"0x{protection:X}",
    };

    /// <summary>
    /// Read <paramref name="length"/> bytes from <paramref name="address"/> in another process.
    /// </summary>
    /// <remarks>
    /// For identifying a region rather than for anything the shim does at run time. When an
    /// unexplained executable allocation turns up, what is in it usually says who made it - an empty
    /// reservation with a small used prefix is a code heap, and a region full of recognisable data is
    /// somebody's buffer.
    /// </remarks>
    public static byte[] ReadRegion(int processId, nint address, int length)
    {
        nint process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, (uint)processId);

        if (process == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess failed for pid {processId}.");

        try
        {
            byte[] buffer = new byte[length];
            unsafe
            {
                fixed (byte* destination = buffer)
                {
                    if (!ReadProcessMemory(process, address, destination, (nuint)length, out nuint read))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory failed at 0x{address:X}.");

                    if (read != (nuint)length)
                        Array.Resize(ref buffer, (int)read);
                }
            }

            return buffer;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadProcessMemory(nint process, nint address, byte* buffer, nuint size, out nuint read);

    /// <summary>
    /// <c>MEMORY_BASIC_INFORMATION</c>, correct on both architectures.
    /// </summary>
    /// <remarks>
    /// The C header carries two explicit <c>__alignment</c> members in its 64-bit form and neither in
    /// its 32-bit form. They are not declared here on purpose: sequential layout aligns
    /// <c>RegionSize</c> naturally, which inserts exactly that padding on x64 and none on x86, so one
    /// declaration is right for both. Writing the padding out by hand would make this struct four
    /// bytes too long on x86 and every field after <c>AllocationProtect</c> would read as garbage -
    /// and a memory scan that silently reports the wrong protections is worse than no scan.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        internal nint BaseAddress;
        internal nint AllocationBase;
        internal uint AllocationProtect;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }
}
