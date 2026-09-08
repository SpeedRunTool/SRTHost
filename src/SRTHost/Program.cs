using System.Runtime.Versioning;
using Avalonia;
using Microsoft.Extensions.Logging;
using SRTHost.Core;

namespace SRTHost;

internal static class Program
{
    // Avalonia needs an STA thread and must not use SynchronizationContext before AppMain runs.
    [STAThread]
    public static int Main(string[] args)
    {
        // Supervision is genuinely Windows-only - it rests on job objects and named pipes - while
        // the Avalonia shell is not. Guarded rather than annotated away at the entry point, so the
        // net10.0 TFM keeps the portability it was chosen for.
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("SRT Host requires Windows: the supervisor uses job objects to contain its runners.");
            return 1;
        }

        // --headless runs the router and supervisor with no window, for OBS setups, autostart and
        // automated tests. It is the same HostRuntime the shell drives, not a second implementation,
        // so anything that works here works there.
        if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
            return HeadlessHost.RunAsync(args).GetAwaiter().GetResult();

        HostSettings settings = ApplyOverrides(HostSettings.Load(), args);

        HostContext host = new(settings);

        try
        {
            return BuildAvaloniaApp(host).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // The lifetime's ShutdownRequested handler is what normally stops the runners; this is
            // the backstop for the paths that never reach it - a startup failure, or a platform
            // Avalonia cannot initialise on. The job object is the backstop for even that.
            host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Applies the command-line overrides the shell honours.
    /// </summary>
    /// <remarks>
    /// Overrides rather than settings: they are not written back to <c>host.json</c>. A shortcut
    /// passing <c>--minimized</c> should not silently become the user's saved preference, and the
    /// sign-in entry this application writes for itself passes exactly that flag.
    /// </remarks>
    private static HostSettings ApplyOverrides(HostSettings settings, string[] args)
    {
        if (args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            settings = settings with { StartMinimized = true };

        if (Argument(args, "--plugins-dir") is { Length: > 0 } directory)
            settings = settings with { PluginsDirectory = directory };

        if (Enum.TryParse(Argument(args, "--log-level"), ignoreCase: true, out LogLevel level))
            settings = settings with { LogLevel = level };

        if (Enum.TryParse(Argument(args, "--runner-log-level"), ignoreCase: true, out LogLevel runnerLevel))
            settings = settings with { RunnerLogLevel = runnerLevel };

        return settings;
    }

    /// <summary>Reads <c>--key value</c> or <c>--key=value</c>, matching the runner's own parser.</summary>
    private static string? Argument(string[] args, string key)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return args[index][(key.Length + 1)..];

            if (string.Equals(args[index], key, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1];
        }

        return null;
    }

    /// <summary>Builds the application over a constructed host.</summary>
    [SupportedOSPlatform("windows")]
    private static AppBuilder BuildAvaloniaApp(HostContext host)
        => AppBuilder.Configure(() => new App(host))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // Referenced by name from the Avalonia XAML previewer, which builds the app with no host; do not
    // rename, and do not give it a host - the previewer runs this in its own process.
    [SupportedOSPlatform("windows")]
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
