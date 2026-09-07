using SRTHost.Core;

namespace SRTHost.PluginRunner;

/// <summary>
/// Entry point for a plugin runner process. Each runner hosts exactly one plugin and talks to the
/// router in SRTHost.exe over a named pipe.
/// </summary>
internal static class Program
{
#if x64
    private const string Architecture = "x64";
#else
    private const string Architecture = "x86";
#endif

    private static async Task<int> Main(string[] args)
    {
        // Phase 0 scaffolding: prove the platform-conditional build works and the shared libraries
        // resolve. The pipe client, plugin load context and message loop land in Phase 2/3.
        await Task.CompletedTask;

        Console.WriteLine($"SRT Host Plugin Runner ({Architecture})");
        Console.WriteLine(SigningInfo.Describe(Environment.ProcessPath ?? string.Empty));
        Console.WriteLine($"Arguments: {(args.Length == 0 ? "(none)" : string.Join(' ', args))}");
        return 0;
    }
}
