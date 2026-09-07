using System.Runtime.InteropServices;
using Avalonia;

namespace SRTHost;

internal static class Program
{
    // Avalonia needs an STA thread and must not use SynchronizationContext before AppMain runs.
    [STAThread]
    public static int Main(string[] args)
    {
        // --headless runs the router with no window, for OBS/autostart and CI smoke tests. The
        // router is a plain hosted service, so this costs almost nothing. Wired up in Phase 4.
        if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
        {
            // The router supervises runners of both bitnesses, so its own architecture is worth
            // stating up front: this process is AnyCPU and should report X64 on 64-bit Windows.
            Console.WriteLine($"SRT Host (headless) - {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}");
            Console.WriteLine("Router not yet implemented.");
            return 0;
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
