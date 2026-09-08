using CommunityToolkit.Mvvm.ComponentModel;

namespace SRTHost.ViewModels;

/// <summary>
/// Base for every page in the shell.
/// </summary>
/// <remarks>
/// <see cref="Title"/> is what the nav rail shows, and it lives on the base class so the rail can
/// bind to a list of pages without knowing what any of them are. That is also what makes
/// <c>ViewLocator</c> enough: the shell never names a view type.
/// </remarks>
public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>What the nav rail calls this page.</summary>
    public abstract string Title { get; }

    /// <summary>Called when the page becomes visible.</summary>
    /// <remarks>
    /// Exists so a page can attach something expensive only while it is on screen - the data
    /// inspector's tap on the router being the case that motivated it, since it copies every frame
    /// the host routes.
    /// </remarks>
    public virtual void Activated() { }

    /// <summary>Called when the page is navigated away from.</summary>
    public virtual void Deactivated() { }
}
