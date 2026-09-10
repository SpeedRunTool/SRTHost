using System.Runtime.InteropServices;

namespace SRTOverlay.Injection;

/// <summary>The Win32 surface the injector uses, source-generated so nothing marshals at run time.</summary>
internal static unsafe partial class NativeMethods
{
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_VM_READ = 0x0010;
    internal const uint PROCESS_VM_WRITE = 0x0020;
    internal const uint PROCESS_VM_OPERATION = 0x0008;
    internal const uint PROCESS_CREATE_THREAD = 0x0002;

    /// <summary>
    /// Exactly what injection needs and nothing more.
    /// </summary>
    /// <remarks>
    /// Notably absent is <c>PROCESS_DUP_HANDLE</c>. The shared textures are published by name and
    /// opened with <c>OpenSharedHandleByName</c> precisely so that this right never has to be asked
    /// for - see section 11. Widening it would be the kind of quiet escalation that costs the whole
    /// ecosystem reputation.
    /// </remarks>
    internal const uint InjectionAccess =
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_CREATE_THREAD;

    internal const uint MEM_COMMIT = 0x1000;
    internal const uint MEM_RESERVE = 0x2000;
    internal const uint MEM_RELEASE = 0x8000;
    internal const uint PAGE_READWRITE = 0x04;

    internal const uint INFINITE = 0xFFFFFFFF;
    internal const uint WAIT_OBJECT_0 = 0;

    internal const uint TH32CS_SNAPMODULE = 0x00000008;
    internal const uint TH32CS_SNAPMODULE32 = 0x00000010;

    internal const int MAX_MODULE_NAME32 = 255;
    internal const int MAX_PATH = 260;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process(nint process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateRemoteThread(nint process, nint attributes, nuint stackSize, nint startAddress, nint parameter, uint creationFlags, nint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeThread(nint thread, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string moduleName);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetProcAddress(nint module, string procName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Module32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Module32First(nint snapshot, ref ModuleEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Module32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Module32Next(nint snapshot, ref ModuleEntry32 entry);

    /// <summary>
    /// <c>MODULEENTRY32W</c>, laid out with inline character buffers so the struct stays blittable.
    /// </summary>
    /// <remarks>
    /// <c>ByValTStr</c> would be the usual way to write the two string fields and would force this
    /// onto <c>DllImport</c>'s run-time marshaller. Inline arrays keep the whole file on
    /// <c>LibraryImport</c>, which matters because an accidental <c>DllImport</c> in a file that is
    /// otherwise all source-generated is easy to miss.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ModuleEntry32
    {
        internal uint dwSize;
        internal uint th32ModuleID;
        internal uint th32ProcessID;
        internal uint GlblcntUsage;
        internal uint ProccntUsage;
        internal nint modBaseAddr;
        internal uint modBaseSize;
        internal nint hModule;
        internal fixed char szModule[MAX_MODULE_NAME32 + 1];
        internal fixed char szExePath[MAX_PATH];

        internal string ModuleName
        {
            get
            {
                fixed (char* name = szModule)
                    return new string(name);
            }
        }

        internal string ExePath
        {
            get
            {
                fixed (char* path = szExePath)
                    return new string(path);
            }
        }
    }
}
