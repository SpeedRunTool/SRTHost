namespace SRTHost.PluginRunner.Tests;

/// <summary>
/// Finds the runner executable to drive.
/// </summary>
/// <remarks>
/// The tests are AnyCPU and the runner is not - it is one project built twice, into
/// <c>SRTHost.PluginRunner64.exe</c> and <c>SRTHost.PluginRunner32.exe</c>, each in its own
/// per-platform output directory. This project builds both, so at least one is always present; which
/// one gets driven is decided here rather than by the test's own bitness.
/// <para>
/// 64-bit is preferred because it is what the router will pick by default, and the 32-bit leg is
/// used when only it was built - which is what a <c>-p:Platform=x86</c> build of the solution looks
/// like. Set <c>SRT_RUNNER_ARCH=x86</c> to drive the 32-bit runner even when both are present, which
/// is how the 32-bit leg gets exercised on a machine that has just built everything.
/// </para>
/// </remarks>
internal static class RunnerLocator
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private static readonly Lazy<string> Located = new(Locate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Full path of the runner executable these tests drive.</summary>
    public static string ExecutablePath => Located.Value;

    private static string Locate()
    {
        string repositoryRoot = FindRepositoryRoot();

        string x64 = Path.Combine(
            repositoryRoot, "src", "SRTHost.PluginRunner", "bin", Configuration, "x64", "net10.0", "win-x64", "SRTHost.PluginRunner64.exe");

        string x86 = Path.Combine(
            repositoryRoot, "src", "SRTHost.PluginRunner", "bin", Configuration, "x86", "net10.0", "win-x86", "SRTHost.PluginRunner32.exe");

        string[] candidates =
            string.Equals(Environment.GetEnvironmentVariable("SRT_RUNNER_ARCH"), "x86", StringComparison.OrdinalIgnoreCase)
                ? [x86, x64]
                : [x64, x86];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "No plugin runner executable was found. Build src/SRTHost.PluginRunner for x64 or x86 first. Looked in:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, candidates));
    }

    /// <summary>
    /// Walks up from the test assembly until it finds the directory holding both <c>src</c> and
    /// <c>tests</c>.
    /// </summary>
    /// <remarks>
    /// Cheaper and steadier than counting <c>..</c> segments, which is a constant that quietly
    /// becomes wrong the first time an output path grows or loses a segment - and this repository's
    /// output paths carry a configuration, a platform, a target framework and sometimes a runtime
    /// identifier.
    /// </remarks>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No directory containing both 'src' and 'tests' above '{AppContext.BaseDirectory}'.");
    }
}
