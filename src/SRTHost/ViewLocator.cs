using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SRTHost.ViewModels;

namespace SRTHost;

/// <summary>
/// Maps <c>FooViewModel</c> to <c>Views.FooView</c> so the shell can show a page it does not name.
/// </summary>
/// <remarks>
/// Registered once as a data template on the application, which is what lets the nav rail bind a
/// <see cref="ContentControl"/> straight to a view model and get the right control back. Without it
/// every page would have to be listed in XAML twice - once as a view model and once as a
/// <c>DataTemplate</c> - and the two lists would drift.
/// <para>
/// This is the only reflection in the shell. Everything else - the observable properties, the
/// commands, the JSON - is source-generated, which is deliberate: it keeps the door open to trimming
/// the host later without discovering then that the UI cannot survive it.
/// </para>
/// </remarks>
public sealed class ViewLocator : IDataTemplate
{
    /// <inheritdoc />
    public bool Match(object? data) => data is ViewModelBase;

    /// <inheritdoc />
    public Control Build(object? data)
    {
        if (data is null)
            return new TextBlock { Text = "No page." };

        string name = data.GetType().FullName!
            .Replace("ViewModels.", "Views.", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        Type? type = Type.GetType(name);

        return type is null
            ? new TextBlock { Text = $"No view for {name}." }
            : (Control)Activator.CreateInstance(type)!;
    }
}
