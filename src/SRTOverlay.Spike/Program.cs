using System.Diagnostics;
using System.Runtime.Versioning;
using SRTOverlay.DirectX12;
using SRTOverlay.Injection;
using SRTOverlay.Protocol;

namespace SRTOverlay.Spike;

/// <summary>
/// Drives one overlay spike gate by hand: find the game, inject the shim, say what happened.
/// </summary>
/// <remarks>
/// <para>
/// Section 11 of the 5.0 plan sets out seven gates, and every one of them has to be judged against a
/// running game by a person looking at it. This is the thing that starts them. It is deliberately a
/// console application with an exit code rather than a test, because "the game is unharmed" is not a
/// proposition a test runner can assert.
/// </para>
/// <para>
/// Arguments, all optional except the target:
/// <code>
/// --process &lt;name.exe|pid&gt;   the game to inject into
/// --shim &lt;path&gt;              overrides the shim next to this executable
/// --log &lt;path&gt;               where the shim writes its log; defaults beside this executable
/// --session &lt;id&gt;             an identifier to stamp into that log
/// --pipe &lt;name&gt;              pipe back to a runner; the injection spike passes none
/// --owner &lt;pid&gt;              process the shim should follow down; 0 or absent means nobody
/// --unsigned                  skip the Authenticode check, for a development build of the shim
/// --claim-process &lt;name&gt;     lie to the shim about which process it is in, to prove it refuses
/// --claim-version &lt;n&gt;        lie about the protocol version, likewise
/// --scan-rwx                  count writable+executable regions, separately for each phase
/// --scan-only [seconds]       scan twice, seconds apart, injecting nothing - the control for the above
/// --dump-rwx                  with either scan, list every region rather than only the new ones
/// --read-region &lt;hex&gt;        summarise what is in a region, to identify who allocated it
/// --any-exec                  count every executable region, not only the writable ones
/// --probe-first               call the do-nothing export before starting, and scan around it
/// --stop                      tell a shim already in the target to detach, and exit
/// </code>
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        Dictionary<string, string> options = ParseArguments(args);
        Dump = options.ContainsKey("dump-rwx");
        AnyExecutable = options.ContainsKey("any-exec");

        // A GPU self-test of the risky interop, in this throwaway process rather than in a game.
        if (options.ContainsKey("selfcheck"))
            return OverlaySelfCheck.Run(Console.WriteLine) ? 0 : 1;

        // Step 3's producer needs no game process of its own: it reads the shim's request block, which
        // carries the adapter and the back buffer size, and creates the surface from that.
        if (options.ContainsKey("produce-surface"))
            return ProduceSurface(options);

        if (options.ContainsKey("help") || !options.TryGetValue("process", out string? target))
        {
            Console.Error.WriteLine(
                "usage: SRTOverlay.Spike64 --process <name.exe|pid> [--shim <path>] [--log <path>] " +
                "[--session <id>] [--pipe <name>] [--owner <pid>] [--unsigned] " +
                "[--claim-process <name>] [--claim-version <n>] [--scan-rwx] [--scan-only [seconds]] " +
                "[--dump-rwx] [--any-exec] [--read-region <hex>] [--probe-first] [--stop] " +
                "[--brightness <n>] [--pq]");
            Console.Error.WriteLine(
                "   or: SRTOverlay.Spike64 --produce-surface --session <id>   (step 3: create the " +
                "shared surface the injected shim composites)");
            Console.Error.WriteLine(
                "   or: SRTOverlay.Spike64 --selfcheck   (exercise the D3D12 interop in-process, no game)");
            return 2;
        }

        try
        {
            Process game = FindProcess(target);

            if (options.TryGetValue("read-region", out string? regionAddress))
                return ReadRegion(game, regionAddress);

            if (options.TryGetValue("scan-only", out string? seconds))
                return ScanOnly(game, seconds);

            string shim = options.TryGetValue("shim", out string? shimPath)
                ? shimPath
                : Path.Combine(AppContext.BaseDirectory, OverlayProtocol.ShimFileName);

            string log = options.TryGetValue("log", out string? logPath)
                ? logPath
                : Path.Combine(AppContext.BaseDirectory, "SRTOverlay.log");

            bool scan = options.ContainsKey("scan-rwx");
            List<MemoryMap.ExecutableRegion> baseline = scan ? Scan(game.Id) : [];

            if (scan)
                Console.WriteLine($"{Kind} regions before anything: {baseline.Count}");

            OverlayInjector injector = new(Console.WriteLine)
            {
                RequireSignature = !options.ContainsKey("unsigned"),

                // Scanning between the two phases is what turns "the injection allocated a page" into
                // "LoadLibraryW allocated it" or "managed startup did" - different problems with
                // different owners, and otherwise indistinguishable without restarting the game.
                AfterMap = scan
                    ? _ => baseline = ReportNewExecutableRegions(game.Id, baseline, "LoadLibraryW alone")
                    : null,

                ProbeFirst = options.ContainsKey("probe-first"),
                AfterProbe = scan
                    ? () => baseline = ReportNewExecutableRegions(game.Id, baseline, "starting the managed runtime")
                    : null,
            };

            if (options.ContainsKey("stop"))
            {
                bool detached = injector.Detach(game.Id, shim);
                if (scan)
                    ReportNewExecutableRegions(game.Id, baseline, "the detach");

                return detached ? 0 : 1;
            }

            OverlayStartupOptions startup = new()
            {
                // The two --claim flags exist to exercise the shim's refusals. There is no other way
                // to prove "a mis-aimed injection stops itself" than to aim one deliberately.
                ProtocolVersion = options.TryGetValue("claim-version", out string? version)
                    ? int.Parse(version)
                    : OverlayProtocol.Version,
                TargetProcess = options.GetValueOrDefault(
                    "claim-process",
                    Path.GetFileName(game.MainModule?.FileName ?? game.ProcessName + ".exe")),
                PipeName = options.GetValueOrDefault("pipe", string.Empty),
                LogPath = log,
                SessionId = options.GetValueOrDefault("session", $"spike-{Environment.ProcessId}"),

                // Defaults to nobody, because this driver exits as soon as it has injected and the
                // shim would follow it straight back out. A real overlay runner passes its own pid.
                OwnerProcessId = options.TryGetValue("owner", out string? owner) ? int.Parse(owner) : 0,

                // Live HDR tuning: override the per-format brightness, and force PQ for an HDR10 buffer.
                OverlayBrightness = options.TryGetValue("brightness", out string? b) ? float.Parse(b) : null,
                OverlayForcePq = options.ContainsKey("pq") ? true : null,
            };

            Console.WriteLine($"Target      {startup.TargetProcess} (pid {game.Id})");
            Console.WriteLine($"Shim        {shim}");
            Console.WriteLine($"Shim log    {log}");
            Console.WriteLine();

            OverlayInjector.InjectionResult result = injector.Inject(game.Id, shim, startup);

            if (scan)
                ReportNewExecutableRegions(game.Id, baseline, "the start export");

            Console.WriteLine();
            Console.WriteLine(result.Started
                ? $"Overlay running in pid {game.Id}. The gate is whether the game is unharmed - go and look."
                : $"Overlay did not start: {result.StartResult}. See {log}.");

            // Prove the game survived the injection, which is the actual step 1 gate. A process that
            // died inside LoadLibrary usually dies immediately, so a short wait catches nearly all of
            // it; the rest is what the person watching the game is for.
            Thread.Sleep(2000);
            game.Refresh();
            Console.WriteLine(game.HasExited
                ? $"*** pid {game.Id} EXITED after injection (exit code {game.ExitCode}). ***"
                : $"pid {game.Id} is still running.");

            return result.Started && !game.HasExited ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Run the throwaway producer for step 3: create the shared surface the injected shim asked for
    /// and animate a colour wash into it until Ctrl+C.
    /// </summary>
    /// <remarks>
    /// The session must match the <c>--session</c> the shim was injected with, because that is what
    /// names the request block, the publish block and the shared handles. There is no game process to
    /// find here - the shim's request block names the adapter, and the surface is created on it.
    /// </remarks>
    private static int ProduceSurface(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("session", out string? session) || string.IsNullOrEmpty(session))
        {
            Console.Error.WriteLine("--produce-surface needs --session <id>, matching the injected shim's session.");
            return 2;
        }

        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;      // let the loop drain the GPU and clear Alive rather than dying
            cancellation.Cancel();
        };

        Console.WriteLine($"Producer for session '{session}'. Ctrl+C to stop.");
        SharedSurfaceProducer producer = new(session, Console.WriteLine);
        bool ok = producer.Run(cancellation.Token);

        Console.WriteLine(ok
            ? "Producer stopped. The gate is whether the wash appeared in the game - go and look."
            : "Producer could not create the surface. See the messages above.");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Scan twice with nothing injected in between, and report what the target changed by itself.
    /// </summary>
    /// <remarks>
    /// The control for <c>--scan-rwx</c>, and it is not optional rigour. A game with a scripting
    /// engine, a shader compiler or a mod framework in it allocates and frees executable memory
    /// continuously - re9.exe carries several hundred such regions - so a region that appears across
    /// an injection is only evidence against the shim if regions do <i>not</i> appear across an equal
    /// stretch of the game simply running. Measuring that costs one flag and settles the question the
    /// other way round: it is the difference between "the shim allocated a page" and "the game did".
    /// </remarks>
    private static int ScanOnly(Process game, string? seconds)
    {
        double delay = string.IsNullOrEmpty(seconds) ? 2 : double.Parse(seconds);

        Console.WriteLine($"Control scan of pid {game.Id}, {delay:0.#} s apart, injecting nothing.");

        List<MemoryMap.ExecutableRegion> before = Scan(game.Id);
        Console.WriteLine($"{Kind} regions before: {before.Count}");

        Thread.Sleep(TimeSpan.FromSeconds(delay));

        ReportNewExecutableRegions(game.Id, before, "the target itself");

        if (Dump)
        {
            Console.WriteLine();
            Console.WriteLine($"All {Kind.ToLowerInvariant()} regions:");
            foreach (MemoryMap.ExecutableRegion region in Scan(game.Id))
                Console.WriteLine($"    {region.Describe()}");
        }

        return 0;
    }

    /// <summary>Whether <c>--dump-rwx</c> was passed.</summary>
    private static bool Dump { get; set; }

    /// <summary>Whether <c>--any-exec</c> widened the scan to every executable region.</summary>
    private static bool AnyExecutable { get; set; }

    /// <summary>How the current scan describes itself, so the output never overstates what it counted.</summary>
    private static string Kind => AnyExecutable ? "Executable" : "Writable+executable";

    /// <summary>The scan the flags asked for.</summary>
    private static List<MemoryMap.ExecutableRegion> Scan(int processId)
        => AnyExecutable
            ? MemoryMap.FindExecutableRegions(processId)
            : MemoryMap.FindWritableExecutableRegions(processId);

    /// <summary>
    /// Report any writable-and-executable region that appeared since <paramref name="before"/>, and
    /// return the new baseline so several phases can be measured in sequence.
    /// </summary>
    /// <remarks>
    /// The gate is the difference, not the total: a game allocates RWX for its own reasons - script
    /// engines, shader compilers, whatever middleware it ships - and comparing against zero would fail
    /// every time while telling nobody anything. Regions are matched by base address, so a region the
    /// game grew is not counted as one that was created.
    /// </remarks>
    private static List<MemoryMap.ExecutableRegion> ReportNewExecutableRegions(
        int processId, List<MemoryMap.ExecutableRegion> before, string what)
    {
        HashSet<nint> known = [.. before.Select(static region => region.BaseAddress)];
        List<MemoryMap.ExecutableRegion> now = Scan(processId);
        List<MemoryMap.ExecutableRegion> added = [.. now.Where(region => !known.Contains(region.BaseAddress))];

        Console.WriteLine();
        if (added.Count == 0)
        {
            Console.WriteLine($"{Kind} regions added by {what}: none.");
        }
        else
        {
            Console.WriteLine($"*** {Kind} regions added by {what}: {added.Count} ***");
            foreach (MemoryMap.ExecutableRegion region in added)
                Console.WriteLine($"    {region.Describe()}");
        }

        return now;
    }

    /// <summary>
    /// Summarise what is in a region, which is usually enough to say who allocated it.
    /// </summary>
    /// <remarks>
    /// Deliberately a summary rather than a full dump. The question is the shape - how much of a
    /// reservation is actually written, and whether the written part looks like code - and a megabyte
    /// of hex answers that no better than a window plus a count of what is non-zero.
    /// </remarks>
    private static int ReadRegion(Process game, string address)
    {
        string trimmed = address.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        nint start = (nint)Convert.ToUInt64(trimmed, 16);
        const int Window = 64 * 1024;

        byte[] bytes = MemoryMap.ReadRegion(game.Id, start, Window);
        int nonZero = bytes.Count(static b => b != 0);
        int lastNonZero = Array.FindLastIndex(bytes, static b => b != 0);

        Console.WriteLine($"Region 0x{start:X} in pid {game.Id}: read {bytes.Length:N0} bytes.");
        Console.WriteLine($"  non-zero bytes  {nonZero:N0} ({(double)nonZero / bytes.Length:P2})");
        Console.WriteLine($"  last non-zero   {(lastNonZero < 0 ? "none - the whole window is zero" : $"offset 0x{lastNonZero:X}")}");
        Console.WriteLine();
        Console.WriteLine("  first 160 bytes:");
        DumpHex(bytes.AsSpan(0, Math.Min(160, bytes.Length)), start);

        return 0;
    }

    private static void DumpHex(ReadOnlySpan<byte> bytes, nint origin)
    {
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            byte[] row = bytes[offset..Math.Min(offset + 16, bytes.Length)].ToArray();
            string hex = string.Join(" ", row.Select(static b => b.ToString("X2")));
            string text = string.Concat(row.Select(static b => b is >= 0x20 and < 0x7F ? (char)b : '.'));
            Console.WriteLine($"    0x{origin + offset:X16}  {hex,-47}  {text}");
        }
    }

    /// <summary>Find a process by pid or by executable name.</summary>
    /// <remarks>
    /// Refuses an ambiguous name rather than picking one. Two copies of a game running at once is
    /// unusual but injecting into whichever the enumeration happened to return first is the sort of
    /// thing that costs an hour when it happens.
    /// </remarks>
    private static Process FindProcess(string target)
    {
        if (int.TryParse(target, out int pid))
            return Process.GetProcessById(pid);

        string name = Path.GetFileNameWithoutExtension(target);
        Process[] matches = Process.GetProcessesByName(name);

        return matches.Length switch
        {
            0 => throw new InvalidOperationException($"No running process named '{name}'."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"{matches.Length} processes named '{name}' are running: " +
                $"{string.Join(", ", matches.Select(static process => process.Id))}. Pass a pid."),
        };
    }

    /// <summary>Parse <c>--key value</c> and <c>--flag</c>, matching the host's own argument style.</summary>
    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            string key = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            parsed[key] = hasValue ? args[++i] : string.Empty;
        }

        return parsed;
    }
}
