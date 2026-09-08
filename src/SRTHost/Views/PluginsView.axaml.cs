using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SRTHost.Views;

/// <summary>The plugin list and its detail pane.</summary>
public partial class PluginsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PluginsView() => AvaloniaXamlLoader.Load(this);
}
