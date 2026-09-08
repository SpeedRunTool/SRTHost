using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SRTHost.Core.Supervision;

/// <summary>
/// A Windows job object that kills every runner assigned to it when the host's handle closes.
/// </summary>
/// <remarks>
/// This is the only mechanism that survives the ways a host actually dies. Graceful shutdown already
/// stops its runners; what this covers is everything else - an unhandled exception, End Task from
/// Task Manager, an MSI upgrade's <c>util:CloseApplication</c>, a debugger stop, a power-loss-adjacent
/// forced kill. In every one of those the host runs no cleanup code at all, and without a job object
/// the runners simply keep going: orphan processes holding a handle on the user's game, invisible in
/// any UI, and still there the next time the host starts and tries to create the same pipe names.
/// <para>
/// The kernel closes every handle a dying process owns, and
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> turns that into "terminate everything in the job". No
/// host code has to run for it to work, which is exactly the property required.
/// </para>
/// <para>
/// A process is assigned <em>after</em> it starts rather than created inside the job. That is a
/// deliberately small race - the window is between <c>Process.Start</c> returning and the assignment
/// a few microseconds later - and the alternative, <c>CreateProcess</c> with
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, means abandoning <see cref="Process.Start"/> and its
/// stream redirection for a hand-rolled <c>STARTUPINFOEX</c>. The runner also exits on its own when
/// its pipe breaks, so the race window is covered twice over.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class JobObject : IDisposable
{
    private readonly SafeJobHandle handle;

    /// <summary>Creates an anonymous job object configured to kill its members on close.</summary>
    /// <exception cref="InvalidOperationException">Windows refused to create or configure it.</exception>
    public JobObject()
    {
        // Anonymous - a named job could be opened, and joined, by anything else running as this
        // user. There is no reason for anyone outside this process to reach it.
        handle = CreateJobObject(IntPtr.Zero, null);

        if (handle.IsInvalid)
            throw new InvalidOperationException("Could not create a job object.", new Win32Exception());

        JobObjectExtendedLimitInformation information = new()
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
                throw new InvalidOperationException("Could not configure the job object.", new Win32Exception());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Puts <paramref name="process"/> into the job, so it cannot outlive this host.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the process had already exited, which is a race rather than a
    /// failure - a runner that died during startup needs no killing.
    /// </returns>
    /// <exception cref="InvalidOperationException">Windows refused the assignment.</exception>
    public bool Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (AssignProcessToJobObject(handle, process.Handle))
            return true;

        Win32Exception error = new();

        // 5 is ERROR_ACCESS_DENIED, which is what assigning an already-dead process reports.
        if (process.HasExited || error.NativeErrorCode == 5 && process.HasExited)
            return false;

        throw new InvalidOperationException(
            $"Could not assign process {process.Id} to the job object.", error);
    }

    /// <inheritdoc />
    public void Dispose() => handle.Dispose();

    /// <summary>Owns the job handle, so it is closed even if this object is finalised.</summary>
    /// <remarks>
    /// A <see cref="SafeHandle"/> rather than an <see cref="IntPtr"/> precisely because closing it
    /// is what kills the runners: a leaked handle would keep the job alive past the host and defeat
    /// the whole mechanism, while a double close would kill them early.
    /// </remarks>
    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    #region Win32

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeJobHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    #endregion
}
