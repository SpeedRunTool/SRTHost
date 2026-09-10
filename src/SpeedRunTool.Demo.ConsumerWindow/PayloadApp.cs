using Avalonia;
using Avalonia.Themes.Fluent;

namespace SpeedRunTool.Demo.ConsumerWindow;

/// <summary>
/// The Avalonia application object for this plugin.
/// </summary>
/// <remarks>
/// Written in code rather than as <c>App.axaml</c> because there is nothing in it but a theme, and a
/// plugin that ships one XAML file is easier to read than one that ships two. A plugin with styles,
/// resources or a data-template locator should use the ordinary <c>App.axaml</c> shape instead;
/// nothing here depends on the choice.
/// <para>
/// Note there is no <c>Application.Lifetime</c>: this application has no main window and never exits
/// on its own. The runner process owns the lifetime, <see cref="AvaloniaUiThread"/> owns the loop, and
/// the plugin owns the window.
/// </para>
/// </remarks>
internal sealed class PayloadApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());
}
