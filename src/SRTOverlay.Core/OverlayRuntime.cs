using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SRTOverlay.Protocol;

namespace SRTOverlay.Core;

/// <summary>
/// What the shim does once it is inside the game, on an ordinary thread with the loader lock released.
/// </summary>
/// <remarks>
/// <para>
/// This is the managed half of the two-step injection described in section 11 of the 5.0 plan: the
/// injector calls <c>LoadLibraryW</c> to map the DLL, whose entry point does nothing, and then makes
/// a second remote thread onto the exported start function - which lands here. Nothing in this class
/// may assume it is running under the loader lock, and nothing may run before it.
/// </para>
/// <para>
/// Deliberately not a <c>DllMain</c> equivalent. A NativeAOT shared library initialises a runtime and
/// a garbage collector on its first managed call, and doing that under the loader lock is exactly the
/// class of deadlock the reference tool's <c>DllMain</c>-to-<c>ThreadMain</c> handoff exists to
/// avoid - only more so.
/// </para>
/// </remarks>
public static class OverlayRuntime
{
    private static int started;
    private static nint ownerHandle;
    private static IOverlayBackend? backend;
    private static ManualResetEventSlim? heartbeatStop;

    /// <summary>The options this shim started with, for the rest of the shim to read.</summary>
    public static OverlayStartupOptions? Options { get; private set; }

    /// <summary>
    /// Start the overlay. Called from the shim's unmanaged start export and from tests.
    /// </summary>
    /// <param name="json">The startup blob, as written into this process by the injector.</param>
    /// <remarks>
    /// Returns rather than throws for every failure, because its caller is an
    /// <c>[UnmanagedCallersOnly]</c> boundary where an escaping exception is a <c>FailFast</c> - and
    /// a <c>FailFast</c> in this process kills someone's game.
    /// </remarks>
    public static OverlayStartResult Start(string? json, IOverlayBackend? graphicsBackend = null)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            return OverlayStartResult.AlreadyRunning;

        if (string.IsNullOrEmpty(json))
            return OverlayStartResult.BadArgument;

        OverlayStartupOptions? options = OverlayStartupOptions.FromJson(json);
        if (options is null)
            return OverlayStartResult.BadArgument;

        if (!string.IsNullOrEmpty(options.LogPath))
            OverlayLog.Open(options.LogPath);

        if (options.ProtocolVersion != OverlayProtocol.Version)
        {
            OverlayLog.Write(
                $"Refusing to start: startup blob states protocol version {options.ProtocolVersion}, " +
                $"this shim implements {OverlayProtocol.Version}.");
            return Refuse(OverlayStartResult.ProtocolMismatch);
        }

        // The mis-aimed-injection guard from section 11. A string compare, checked before any hook
        // is installed, so a shim that landed in the wrong process draws over nothing and leaves.
        string actual = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        if (!string.Equals(actual, options.TargetProcess, StringComparison.OrdinalIgnoreCase))
        {
            OverlayLog.Write(
                $"Refusing to start: this process is '{actual}', the startup blob named " +
                $"'{options.TargetProcess}'.");
            return Refuse(OverlayStartResult.WrongProcess);
        }

        Options = options;
        WriteIdentity(options);
        WatchOwner(options.OwnerProcessId);
        StartBackend(graphicsBackend);

        OverlayLog.Write(backend is null
            ? "Overlay started with no graphics backend; nothing is hooked."
            : $"Overlay started on {backend.Name}.");

        return OverlayStartResult.Success;
    }

    /// <summary>
    /// Detach: release hooks and resources, and stop. The DLL stays resident.
    /// </summary>
    /// <remarks>
    /// It is not an unload and must never be described as one. NativeAOT cannot tear its runtime out
    /// of a live process, so the module stays mapped until the game exits - which is a thing to say
    /// in the UI rather than to paper over with a <c>FreeLibrary</c> that would crash.
    /// </remarks>
    public static void Stop()
    {
        if (Interlocked.Exchange(ref started, 0) == 0)
            return;

        // Closing the handle releases the watchdog's wait, so the thread ends without needing to be
        // signalled separately or, worse, aborted.
        nint owner = Interlocked.Exchange(ref ownerHandle, 0);
        if (owner != 0)
            NativeMethods.CloseHandle(owner);

        heartbeatStop?.Set();

        // Hooks come out before anything else, because everything else exists to serve them and the
        // game may be calling through them right now.
        try
        {
            backend?.Shutdown();
        }
        catch (Exception exception)
        {
            OverlayLog.WriteException("backend shutdown", exception);
        }
        finally
        {
            backend = null;
        }

        OverlayLog.Write("Overlay detached. The module stays resident; NativeAOT cannot unload itself.");
        OverlayLog.Close();
        Options = null;
    }

    /// <summary>
    /// Detach automatically if the process that injected this shim goes away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shim outlives its owner by construction - it lives in a process neither the host nor the
    /// runner controls - so nothing else would notice. Once <c>Present</c> is hooked, an orphaned
    /// shim means a detour in someone's game with nothing alive that could remove it, which is worse
    /// than never having injected. This covers the ways an orderly stop cannot happen at all: the
    /// runner crashing, or the supervisor killing it for not responding.
    /// </para>
    /// <para>
    /// A dedicated background thread blocked on a handle rather than a timer: it costs one thread
    /// that is never scheduled until the moment it matters, and it reacts immediately instead of
    /// within a polling interval. Nothing here runs on the render path.
    /// </para>
    /// </remarks>
    private static void WatchOwner(int ownerProcessId)
    {
        if (ownerProcessId == 0)
        {
            OverlayLog.Write("No owner process given; the overlay will only detach when told to.");
            return;
        }

        nint owner = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE, false, (uint)ownerProcessId);
        if (owner == 0)
        {
            // Most likely the owner already exited between injecting and this point. Refusing to
            // start over it would be worse than running unwatched and saying so.
            OverlayLog.Write($"Could not open owner process {ownerProcessId} to watch it; " +
                             "the overlay will only detach when told to.");
            return;
        }

        Volatile.Write(ref ownerHandle, owner);

        Thread watchdog = new(static () =>
        {
            try
            {
                nint handle = Volatile.Read(ref ownerHandle);
                if (handle == 0)
                    return;

                NativeMethods.WaitForSingleObject(handle, NativeMethods.INFINITE);

                // Stop() closes the handle, so a wait that returns because of that is a normal
                // detach and there is nothing left to do.
                if (Volatile.Read(ref ownerHandle) == 0)
                    return;

                OverlayLog.Write("Owner process exited; detaching.");
                Stop();
            }
            catch (Exception exception)
            {
                // This thread is not an unmanaged boundary, but letting it throw would still take
                // the game down with an unhandled exception on a thread nobody is watching.
                OverlayLog.WriteException("owner watchdog", exception);
            }
        })
        {
            IsBackground = true,
            Name = "SRT Overlay owner watchdog",
        };

        watchdog.Start();
        OverlayLog.Write($"  owner        pid {ownerProcessId}, watched");
    }

    /// <summary>
    /// Bring a graphics backend up, and start the heartbeat that is the only way to observe it.
    /// </summary>
    /// <remarks>
    /// A hook that draws nothing is invisible by construction, so the counters it keeps have to be
    /// reported from somewhere. A background thread on a timer is that somewhere: it formats and
    /// writes, both of which are banned on the render path, and it does so far away from it.
    /// </remarks>
    private static void StartBackend(IOverlayBackend? graphicsBackend)
    {
        if (graphicsBackend is null)
            return;

        bool ready;
        try
        {
            ready = graphicsBackend.Initialize();
        }
        catch (Exception exception)
        {
            // Initialize is documented not to throw; if it does anyway, the overlay runs without a
            // backend rather than taking the game down over it.
            OverlayLog.WriteException($"{graphicsBackend.Name} initialisation", exception);
            return;
        }

        if (!ready)
        {
            OverlayLog.Write($"{graphicsBackend.Name} is not usable in this process; continuing without it.");
            return;
        }

        backend = graphicsBackend;
        heartbeatStop = new ManualResetEventSlim(false);

        Thread heartbeat = new(static () =>
        {
            ManualResetEventSlim? stop = heartbeatStop;
            IOverlayBackend? active = backend;
            if (stop is null || active is null)
                return;

            try
            {
                while (!stop.Wait(TimeSpan.FromSeconds(5)))
                    OverlayLog.Write($"[{active.Name}] {active.DescribeActivity()}");
            }
            catch (Exception exception)
            {
                OverlayLog.WriteException("backend heartbeat", exception);
            }
        })
        {
            IsBackground = true,
            Name = "SRT Overlay heartbeat",
        };

        heartbeat.Start();
    }

    /// <summary>Undo the start latch so a corrected injection can be retried without a restart.</summary>
    private static OverlayStartResult Refuse(OverlayStartResult result)
    {
        Volatile.Write(ref started, 0);
        OverlayLog.Close();
        return result;
    }

    /// <summary>
    /// Say exactly what is running and where it came from, once, at the top of the log.
    /// </summary>
    /// <remarks>
    /// The reference tool does the same thing for the same reason: a crash inside a game arrives as a
    /// log file and nothing else, and the first question is always which build and which copy of the
    /// binary. Answering it here costs one write and saves the guessing.
    /// </remarks>
    private static unsafe void WriteIdentity(OverlayStartupOptions options)
    {
        // The address of a static method compiled into this assembly, which under NativeAOT is a
        // real code address in the shim's own image - so the module it resolves to is the shim.
        delegate*<void> anchor = &Anchor;
        string shimPath = NativeMethods.GetModulePathContaining((nint)anchor, out nint shimBase);

        AssemblyName self = typeof(OverlayRuntime).Assembly.GetName();

        OverlayLog.Write($"SRT Overlay shim, protocol v{OverlayProtocol.Version}.");
        OverlayLog.Write($"  assembly     {self.Name} {self.Version}");
        OverlayLog.Write($"  runtime      {RuntimeInformation.FrameworkDescription} " +
                         $"({RuntimeInformation.ProcessArchitecture}, " +
                         $"{(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")})");
        OverlayLog.Write($"  shim image   {(shimPath.Length == 0 ? "<unknown>" : shimPath)} @ 0x{shimBase:X}");
        OverlayLog.Write($"  host process {Environment.ProcessPath} (pid {Environment.ProcessId})");
        OverlayLog.Write($"  start thread {NativeMethods.GetCurrentThreadId()}");
        OverlayLog.Write($"  session      {(string.IsNullOrEmpty(options.SessionId) ? "<none>" : options.SessionId)}");
        OverlayLog.Write($"  pipe         {(string.IsNullOrEmpty(options.PipeName) ? "<none>" : options.PipeName)}");
    }

    /// <summary>A do-nothing static method that exists to have an address inside this image.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Anchor()
    {
    }
}
