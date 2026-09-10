using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SpeedRunTool.Demo.ConsumerWindow;

/// <summary>
/// The plugin's window.
/// </summary>
/// <remarks>
/// Code-behind holds one thing: the fact that a user closing this window must not stop the plugin.
/// Everything else is in the XAML and the view model.
/// </remarks>
public partial class PayloadWindow : Window
{
    /// <summary>Creates the window over its view model.</summary>
    public PayloadWindow(PayloadViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Parameterless constructor for the XAML previewer.</summary>
    public PayloadWindow()
        : this(new PayloadViewModel())
    {
    }

    /// <summary>Whether a close is the plugin shutting down rather than the user clicking the X.</summary>
    /// <remarks>
    /// A user closing a plugin's window means "hide this", not "stop consuming": the plugin is still
    /// running, the host's plugin list still shows it, and re-opening it is a Restart away. Actually
    /// exiting on that click would leave the host describing a plugin as Running with nothing to
    /// show for it. The plugin sets this before its own close.
    /// </remarks>
    internal bool ClosingForShutdown { get; set; }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!ClosingForShutdown)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
