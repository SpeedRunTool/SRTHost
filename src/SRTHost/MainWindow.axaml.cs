using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SRTHost;

/// <summary>The shell window.</summary>
/// <remarks>
/// The only logic in the code-behind is close-to-tray, which cannot live in a view model: it has to
/// cancel the window's own closing event, and cancelling it is a decision about this window rather
/// than about the host.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Asked, on every close, whether the window should hide instead of closing.
    /// </summary>
    /// <remarks>
    /// A callback rather than a bool property because the setting is live: the user can change it on
    /// the settings page and expect the very next close to honour it, without the window having been
    /// told.
    /// </remarks>
    public Func<bool>? CloseToTray { get; set; }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // ShutdownMode is OnExplicitShutdown, so the process survives this either way; hiding rather
        // than closing keeps the window's state - selected page, log filters, scroll position - for
        // the next time the tray icon is clicked.
        if (!e.IsProgrammatic && CloseToTray?.Invoke() == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
