using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SRTHost.Views;

/// <summary>The SettingsView page.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}
