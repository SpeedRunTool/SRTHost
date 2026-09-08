using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SRTHost.ViewModels;

namespace SRTHost.Views;

/// <summary>
/// The log viewer.
/// </summary>
/// <remarks>
/// Follow-tail is the only thing in the code-behind, and it belongs here: scrolling is a property of
/// this list control, not of the log. The view model owns whether following is on; the view owns how
/// to do it.
/// </remarks>
public partial class LogView : UserControl
{
    private ListBox? entries;

    /// <summary>Creates the view.</summary>
    public LogView()
    {
        AvaloniaXamlLoader.Load(this);

        entries = this.FindControl<ListBox>("Entries");

        DataContextChanged += (_, _) => Attach();

        Attach();
    }

    private void Attach()
    {
        if (DataContext is not LogViewModel model)
            return;

        // The batching drain adds entries in groups, so this fires a few times a second at most even
        // when a plugin is logging at thirty hertz.
        model.Entries.CollectionChanged -= OnEntriesChanged;
        model.Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not LogViewModel { FollowTail: true } model
            || entries is null
            || model.Entries.Count == 0)
        {
            return;
        }

        entries.ScrollIntoView(model.Entries[^1]);
    }
}
