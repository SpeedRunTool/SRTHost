using System.Runtime.InteropServices;

namespace SRTOverlay.Core;

/// <summary>
/// The Win32 surface the shim uses. Source-generated, so nothing marshals at run time.
/// </summary>
/// <remarks>
/// <c>LibraryImport</c> rather than <c>DllImport</c> throughout: the generated marshalling is
/// ordinary C# the ILCompiler can see, where <c>DllImport</c>'s built-in marshalling is a run-time
/// facility that AOT only partly supports. Keeping the whole file on one mechanism means an
/// accidental <c>DllImport</c> stands out.
/// </remarks>
internal static partial class NativeMethods
{
    /// <summary>Resolve a module handle from an address inside it.</summary>
    internal const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;

    /// <summary>Do not add a reference; the caller must not free the returned handle.</summary>
    internal const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetModuleHandleEx(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetModuleFileName(nint module, [Out] char[] fileName, uint size);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    /// <summary>Enough access to wait on a process handle and read its exit code.</summary>
    internal const uint SYNCHRONIZE = 0x00100000;

    internal const uint INFINITE = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>Page protection allowing reads and writes but not execution.</summary>
    internal const uint PAGE_READWRITE = 0x04;

    /// <summary>
    /// Change page protection, used to make one COM vtable slot writable for a single pointer write.
    /// </summary>
    /// <remarks>
    /// Note what this is applied to: a <i>data</i> page holding a vtable, never a code page. Nothing
    /// in the shim ever marks a page executable, and nothing rewrites an instruction - which is the
    /// property section 11 wants to be able to state plainly.
    /// </remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    /// <summary>Full path of the module containing <paramref name="address"/>, or empty.</summary>
    /// <remarks>
    /// Used to log where the shim actually landed. That single line is the difference between "the
    /// injection worked" and "some copy of the shim, from somewhere, is loaded" when a report comes
    /// in from a machine nobody can reach.
    /// </remarks>
    internal static string GetModulePathContaining(nint address, out nint moduleBase)
    {
        moduleBase = 0;

        if (!GetModuleHandleEx(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                address,
                out moduleBase))
        {
            return string.Empty;
        }

        char[] buffer = new char[512];
        uint length = GetModuleFileName(moduleBase, buffer, (uint)buffer.Length);
        return length == 0 ? string.Empty : new string(buffer, 0, (int)length);
    }
}
