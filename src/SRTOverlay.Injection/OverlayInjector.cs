using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SRTOverlay.Protocol;

namespace SRTOverlay.Injection;

/// <summary>
/// Puts the overlay shim into a game process and starts it.
/// </summary>
/// <remarks>
/// <para>
/// Two steps, and the split is the whole point. <c>LoadLibraryW</c> on a remote thread maps the DLL,
/// whose entry point deliberately does nothing; a second remote thread onto the shim's exported start
/// function then runs the real work with the loader lock released. A NativeAOT DLL brings up a
/// runtime and a garbage collector on its first managed call, and doing that inside <c>DllMain</c> is
/// the deadlock this shape exists to avoid.
/// </para>
/// <para>
/// Everything here is deliberately the boring technique. <c>LoadLibraryW</c> from a fixed path, never
/// manual mapping; a signature check before the load; no executable allocation of any kind. Section
/// 11 of the 5.0 plan explains why that is a requirement and not a style preference: manual mapping
/// is the single most malware-associated injection technique there is, and the shim's whole defence
/// is being legible to the tools that are watching.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OverlayInjector
{
    private readonly Action<string> log;

    /// <param name="log">Where progress and refusals go. The runner passes its own logger.</param>
    public OverlayInjector(Action<string>? log = null) => this.log = log ?? (static _ => { });

    /// <summary>
    /// Require the shim to carry a trusted Authenticode signature before it is loaded.
    /// </summary>
    /// <remarks>
    /// Defaults to on and should stay on everywhere except a development build of the shim, which is
    /// unsigned by definition. Turning it off is logged at every injection rather than once, because
    /// a warning that scrolls past during setup is a warning nobody reads.
    /// </remarks>
    public bool RequireSignature { get; init; } = true;

    /// <summary>Substring the signer's subject must contain when <see cref="RequireSignature"/> is set.</summary>
    public string ExpectedSigner { get; init; } = "SpeedRunTool";

    /// <summary>
    /// Called after the shim is mapped and before its start export runs, with the remote module base.
    /// </summary>
    /// <remarks>
    /// A seam for measurement, not for behaviour. Injection has two distinct phases - a
    /// <c>LoadLibraryW</c> that runs the DLL's entry point, and a call into managed code afterwards -
    /// and when something changes in the target, which of the two did it is the first question and is
    /// otherwise unanswerable without restarting the game.
    /// </remarks>
    public Action<nint>? AfterMap { get; init; }

    /// <summary>
    /// Call <see cref="OverlayProtocol.ProbeExport"/> after mapping and before starting, so a
    /// measurement taken around it separates runtime bootstrap from the shim's own startup.
    /// </summary>
    /// <remarks>Diagnostic only. Nothing in production sets it.</remarks>
    public bool ProbeFirst { get; init; }

    /// <summary>Called after the probe export returns, when <see cref="ProbeFirst"/> is set.</summary>
    public Action? AfterProbe { get; init; }

    /// <summary>How long to wait for each remote thread before giving up.</summary>
    /// <remarks>
    /// A bounded wait rather than <c>INFINITE</c>. The reference tool waits forever, which is fine in
    /// a console utility a person is watching and not fine in a plugin runner: a game that stalls
    /// inside the loader would hang the overlay plugin with no way back.
    /// </remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>What <see cref="Inject"/> managed to do.</summary>
    /// <param name="Loaded">Whether the DLL is mapped in the target.</param>
    /// <param name="ModuleBase">Remote base address of the shim, or zero.</param>
    /// <param name="StartResult">What the shim's start export returned.</param>
    public readonly record struct InjectionResult(bool Loaded, nint ModuleBase, OverlayStartResult StartResult)
    {
        /// <summary>Whether the overlay is running in the target.</summary>
        public bool Started => Loaded && StartResult == OverlayStartResult.Success;
    }

    /// <summary>
    /// Load <paramref name="shimPath"/> into <paramref name="processId"/> and start it with
    /// <paramref name="options"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The shim or the target is unusable.</exception>
    /// <exception cref="Win32Exception">A Win32 call the injection depends on failed.</exception>
    public InjectionResult Inject(int processId, string shimPath, OverlayStartupOptions options)
    {
        shimPath = Path.GetFullPath(shimPath);

        if (!File.Exists(shimPath))
            throw new InvalidOperationException($"Overlay shim not found at '{shimPath}'.");

        uint startRva = VerifyShim(shimPath, processId);

        nint process = NativeMethods.OpenProcess(NativeMethods.InjectionAccess, false, (uint)processId);
        if (process == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess failed for pid {processId}.");

        try
        {
            nint moduleBase = LoadRemoteLibrary(process, processId, shimPath);
            AfterMap?.Invoke(moduleBase);

            if (ProbeFirst)
            {
                uint probeRva = PeImage.Read(shimPath).Exports[OverlayProtocol.ProbeExport];
                RunRemote(process, moduleBase + (nint)probeRva, [0], OverlayProtocol.ProbeExport, out uint _);
                log($"{OverlayProtocol.ProbeExport} returned; the managed runtime is now up.");
                AfterProbe?.Invoke();
            }

            OverlayStartResult startResult = StartRemote(process, moduleBase, startRva, options);
            return new InjectionResult(Loaded: true, moduleBase, startResult);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// Tell a shim already inside <paramref name="processId"/> to detach, and (by default) unload it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two remote calls. First <see cref="OverlayProtocol.StopExport"/> makes the shim release its
    /// hooks, release its resources and stop its threads. Then, when <paramref name="unload"/> is set,
    /// <c>FreeLibrary</c> on a remote thread unmaps the module. <b>The C++ shim can genuinely unload</b>
    /// - it has no embedded runtime, unlike the NativeAOT shim it replaced, which could only ever
    /// detach - and because Stop has already removed the hooks, no game vtable still points into the
    /// module when it unmaps. This is what lets a rebuilt shim be reinjected without restarting the
    /// game, which is reason 1 for the move to C++.
    /// </para>
    /// <para>
    /// A remote call rather than a message over the pipe, deliberately. This has to work when the
    /// pipe is the thing that broke, and it is the same machinery the injection already uses - so
    /// there is no second mechanism to keep correct. When the pipe exists it should be tried first,
    /// because an orderly shutdown lets the shim pick its moment; this is the fallback that always
    /// works.
    /// </para>
    /// </remarks>
    /// <param name="unload">
    /// Unmap the module with <c>FreeLibrary</c> after it detaches. The default, and what enables
    /// reinject; pass <see langword="false"/> only to leave a detached shim resident on purpose.
    /// </param>
    /// <returns><see langword="false"/> if the shim is not loaded in that process at all.</returns>
    public bool Detach(int processId, string shimPath, bool unload = true)
    {
        shimPath = Path.GetFullPath(shimPath);

        nint moduleBase = FindRemoteModule(processId, Path.GetFileName(shimPath));
        if (moduleBase == 0)
        {
            log($"'{Path.GetFileName(shimPath)}' is not loaded in pid {processId}; nothing to detach.");
            return false;
        }

        // No signature check here. The point of verifying is to decide whether to LOAD something;
        // this module is already in the process, and refusing to stop it would leave hooks installed
        // that nothing can then remove.
        if (!PeImage.Read(shimPath).Exports.TryGetValue(OverlayProtocol.StopExport, out uint stopRva))
            throw new InvalidOperationException($"'{shimPath}' does not export {OverlayProtocol.StopExport}.");

        nint process = NativeMethods.OpenProcess(NativeMethods.InjectionAccess, false, (uint)processId);
        if (process == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess failed for pid {processId}.");

        try
        {
            // The stop export takes no argument, but CreateRemoteThread always passes one and
            // RunRemote always allocates one. A single zero byte is the cheapest way to keep one code
            // path for both calls rather than a second, subtly different one.
            RunRemote(process, moduleBase + (nint)stopRva, [0], OverlayProtocol.StopExport, out uint exitCode);

            if (!unload)
            {
                log($"{OverlayProtocol.StopExport} returned {exitCode}. The shim is detached; the module was left resident.");
                return true;
            }

            bool unmapped = FreeRemoteLibrary(process, processId, moduleBase, Path.GetFileName(shimPath));
            log($"{OverlayProtocol.StopExport} returned {exitCode}; module " +
                (unmapped ? "unloaded. A rebuilt shim can now be reinjected." : "detached but still resident - the unload did not take (see above)."));
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// Unmap the shim from the target with a remote <c>FreeLibrary</c>, and confirm it is gone.
    /// </summary>
    /// <remarks>
    /// Not through <see cref="RunRemote"/>: <c>FreeLibrary</c> takes the <c>HMODULE</c> by value, which
    /// is the module base, so it is passed as the thread's own parameter rather than as a pointer to a
    /// written buffer. Safe only after the stop export has removed the hooks - which is why <see
    /// cref="Detach"/> calls Stop first and this second.
    /// </remarks>
    /// <returns><see langword="true"/> if the module is no longer among the target's modules.</returns>
    private bool FreeRemoteLibrary(nint process, int processId, nint moduleBase, string moduleName)
    {
        nint freeLibrary = NativeMethods.GetProcAddress(
            NativeMethods.GetModuleHandleW("kernel32.dll"), "FreeLibrary");
        if (freeLibrary == 0)
        {
            log("Could not resolve FreeLibrary; the shim is detached but the module was left resident.");
            return false;
        }

        nint thread = NativeMethods.CreateRemoteThread(process, 0, 0, freeLibrary, moduleBase, 0, 0);
        if (thread == 0)
        {
            log($"CreateRemoteThread(FreeLibrary) failed ({Marshal.GetLastWin32Error()}); module left resident.");
            return false;
        }

        try
        {
            if (NativeMethods.WaitForSingleObject(thread, (uint)Timeout.TotalMilliseconds) != NativeMethods.WAIT_OBJECT_0)
            {
                log("FreeLibrary did not return in time; the module may still be resident.");
                return false;
            }
        }
        finally
        {
            NativeMethods.CloseHandle(thread);
        }

        // Confirm rather than trust: the module should no longer be among the process's modules. A
        // still-present module means another reference is held (a second injection that never
        // unloaded), which is worth knowing rather than assuming away.
        return FindRemoteModule(processId, moduleName) == 0;
    }

    /// <summary>
    /// Check the shim is trustworthy, is the right architecture for the target, and exports what the
    /// injector is about to call. Returns the start export's RVA.
    /// </summary>
    /// <remarks>
    /// All three checks happen before <c>OpenProcess</c>, so a bad shim never causes a handle to a
    /// game to be opened at all.
    /// </remarks>
    private uint VerifyShim(string shimPath, int processId)
    {
        if (RequireSignature)
        {
            Authenticode.Result trust = Authenticode.Verify(shimPath, ExpectedSigner);
            log($"Shim signature: {trust.Describe()}");

            if (!trust.Trusted)
            {
                throw new InvalidOperationException(
                    $"Refusing to inject '{shimPath}': {trust.Describe()}. " +
                    "Set RequireSignature=false only when developing the shim itself.");
            }
        }
        else
        {
            log($"Shim signature check DISABLED for '{shimPath}'. This is a development-only setting.");
        }

        PeImage.PeInfo shim = PeImage.Read(shimPath);

        bool targetIs64Bit = IsProcess64Bit(processId);
        bool shimIs64Bit = shim.Machine == PeImage.MachineAmd64;
        if (targetIs64Bit != shimIs64Bit)
        {
            throw new InvalidOperationException(
                $"Architecture mismatch: '{Path.GetFileName(shimPath)}' is " +
                $"{(shimIs64Bit ? "64-bit" : "32-bit")} and pid {processId} is " +
                $"{(targetIs64Bit ? "64-bit" : "32-bit")}.");
        }

        // The injecting process has to match too, because CreateRemoteThread across the WOW64
        // boundary does not work. In production it always does - an overlay plugin runs in the
        // runner built for the game's bitness - so this catches a spike driver pointed at the wrong
        // game rather than a real deployment mistake.
        if (Environment.Is64BitProcess != targetIs64Bit)
        {
            throw new InvalidOperationException(
                $"This process is {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} and cannot " +
                $"inject into {(targetIs64Bit ? "64-bit" : "32-bit")} pid {processId}. " +
                "Use the runner built for the game's architecture.");
        }

        if (!shim.Exports.TryGetValue(OverlayProtocol.StartExport, out uint startRva))
        {
            throw new InvalidOperationException(
                $"'{shimPath}' does not export {OverlayProtocol.StartExport}. " +
                $"It exports: {(shim.Exports.Count == 0 ? "<nothing>" : string.Join(", ", shim.Exports.Keys.Order()))}.");
        }

        log($"Shim verified: {Path.GetFileName(shimPath)}, {(shimIs64Bit ? "x64" : "x86")}, " +
            $"{OverlayProtocol.StartExport} at RVA 0x{startRva:X}.");

        return startRva;
    }

    /// <summary>Map the shim with <c>LoadLibraryW</c> on a remote thread and find where it landed.</summary>
    private nint LoadRemoteLibrary(nint process, int processId, string shimPath)
    {
        nint loadLibrary = NativeMethods.GetProcAddress(
            NativeMethods.GetModuleHandleW("kernel32.dll"),
            "LoadLibraryW");

        if (loadLibrary == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve LoadLibraryW.");

        // kernel32 is loaded at the same base in every process in a session, so the local address is
        // the remote address. This is the one assumption in the file worth naming, and it is the same
        // one every injector in existence makes.
        RunRemote(process, loadLibrary, Encoding.Unicode.GetBytes(shimPath + "\0"), "LoadLibraryW", out uint _);

        nint moduleBase = FindRemoteModule(processId, Path.GetFileName(shimPath));
        if (moduleBase == 0)
        {
            throw new InvalidOperationException(
                $"LoadLibraryW returned but '{Path.GetFileName(shimPath)}' is not among pid {processId}'s modules. " +
                "The usual cause is a missing dependency of the shim.");
        }

        log($"Shim mapped in pid {processId} at 0x{moduleBase:X}.");
        return moduleBase;
    }

    /// <summary>Call the shim's start export on a second remote thread, with the startup blob.</summary>
    private OverlayStartResult StartRemote(nint process, nint moduleBase, uint startRva, OverlayStartupOptions options)
    {
        // The packed struct the C++ shim reads, not JSON: a fixed-size blob with no terminator, whose
        // layout OverlayStartupOptions and OverlayProtocol.h agree on byte for byte.
        byte[] blob = options.ToBlob();
        RunRemote(process, moduleBase + (nint)startRva, blob, OverlayProtocol.StartExport, out uint exitCode);

        OverlayStartResult result = (OverlayStartResult)exitCode;
        log($"{OverlayProtocol.StartExport} returned {result} ({exitCode}).");
        return result;
    }

    /// <summary>
    /// Write <paramref name="argument"/> into the target, run <paramref name="routine"/> on a thread
    /// that receives a pointer to it, wait, and clean up.
    /// </summary>
    /// <remarks>
    /// The remote allocation is freed only after the thread has been waited on. The shim's contract
    /// says the pointer is read once and never retained, and freeing it early would make that a
    /// use-after-free in someone's game rather than a bug in a log.
    /// </remarks>
    private unsafe void RunRemote(nint process, nint routine, byte[] argument, string what, out uint exitCode)
    {
        nint remoteArgument = NativeMethods.VirtualAllocEx(
            process, 0, (nuint)argument.Length,
            NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
            NativeMethods.PAGE_READWRITE);

        if (remoteArgument == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"VirtualAllocEx failed for {what}.");

        nint thread = 0;
        try
        {
            fixed (byte* source = argument)
            {
                if (!NativeMethods.WriteProcessMemory(process, remoteArgument, source, (nuint)argument.Length, out nuint written)
                    || written != (nuint)argument.Length)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory failed for {what}.");
                }
            }

            thread = NativeMethods.CreateRemoteThread(process, 0, 0, routine, remoteArgument, 0, 0);
            if (thread == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateRemoteThread failed for {what}.");

            uint wait = NativeMethods.WaitForSingleObject(thread, (uint)Timeout.TotalMilliseconds);
            if (wait != NativeMethods.WAIT_OBJECT_0)
                throw new InvalidOperationException($"{what} did not return within {Timeout.TotalSeconds:0} s.");

            if (!NativeMethods.GetExitCodeThread(thread, out exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetExitCodeThread failed for {what}.");
        }
        finally
        {
            if (thread != 0)
                NativeMethods.CloseHandle(thread);

            NativeMethods.VirtualFreeEx(process, remoteArgument, 0, NativeMethods.MEM_RELEASE);
        }
    }

    /// <summary>Base address of <paramref name="moduleName"/> in the target, or zero.</summary>
    /// <remarks>
    /// Toolhelp rather than the remote thread's exit code, which is where an injector usually gets
    /// the module handle from. That exit code is a <c>DWORD</c>: on x64 it truncates the returned
    /// <c>HMODULE</c> to 32 bits and yields an address that is silently wrong.
    /// </remarks>
    private static nint FindRemoteModule(int processId, string moduleName)
    {
        nint snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.TH32CS_SNAPMODULE | NativeMethods.TH32CS_SNAPMODULE32, (uint)processId);

        if (snapshot == -1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateToolhelp32Snapshot failed for pid {processId}.");

        try
        {
            NativeMethods.ModuleEntry32 entry = default;
            entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>();

            if (!NativeMethods.Module32First(snapshot, ref entry))
                return 0;

            do
            {
                if (string.Equals(entry.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                    return entry.modBaseAddr;
            }
            while (NativeMethods.Module32Next(snapshot, ref entry));

            return 0;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    /// <summary>Whether a process is 64-bit, on the assumption the OS is.</summary>
    private static bool IsProcess64Bit(int processId)
    {
        if (!Environment.Is64BitOperatingSystem)
            return false;

        nint handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION, false, (uint)processId);
        if (handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not query pid {processId}. Run as the same user as the game.");

        try
        {
            return NativeMethods.IsWow64Process(handle, out bool isWow64)
                ? !isWow64
                : throw new Win32Exception(Marshal.GetLastWin32Error(), $"IsWow64Process failed for pid {processId}.");
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
