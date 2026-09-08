using Avalonia;

namespace SRTHost;

internal static class Program
{
    // Avalonia needs an STA thread and must not use SynchronizationContext before AppMain runs.
    [STAThread]
    public static int Main(string[] args)
    {
        // --headless runs the router and supervisor with no window, for OBS setups, autostart and
        // automated tests. It is the same HostRuntime the shell drives, not a second implementation,
        // so anything that works here works there.
        if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
        {
            // Guarded rather than annotated away: supervision is genuinely Windows-only - it rests
            // on job objects and named pipes - while the Avalonia shell below is not, and marking
            // the whole entry point Windows-only would give up the portability the net10.0 TFM was
            // chosen to keep.
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("--headless requires Windows: the supervisor uses job objects to contain its runners.");
                return 1;
            }

            return HeadlessHost.RunAsync(args).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Referenced by name from the Avalonia XAML previewer; do not rename.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
