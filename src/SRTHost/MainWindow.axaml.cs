using System.Reflection;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SRTHost;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Phase 0 scaffolding. The nav rail, plugin list, log viewer and settings forms arrive in
        // Phase 5; for now this only proves the app starts and the shared libraries resolve.
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        this.FindControl<TextBlock>("StatusText")!.Text =
            $"Version {version} — {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
    }
}
