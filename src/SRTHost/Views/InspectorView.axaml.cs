using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SRTHost.Views;

/// <summary>The InspectorView page.</summary>
public partial class InspectorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public InspectorView() => AvaloniaXamlLoader.Load(this);
}
