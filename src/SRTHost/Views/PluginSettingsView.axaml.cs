using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SRTHost.ViewModels;

namespace SRTHost.Views;

/// <summary>
/// A plugin's generated settings form and the JSON view of the same document.
/// </summary>
/// <remarks>
/// Two things here need the view rather than a view model, and both for the same reason - they are
/// questions only a live window can answer. Browsing for a path needs the top level's
/// <see cref="IStorageProvider"/>, and recording a shortcut needs the key press that reaches the
/// focused control. Keeping them here is what lets every setting view model stay a plain object,
/// testable without a rendering stack.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class PluginSettingsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PluginSettingsView() => AvaloniaXamlLoader.Load(this);

    private async void OnBrowse(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TextSettingViewModel setting })
            return;

        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;

        if (storage is null)
            return;

        try
        {
            if (setting.PickDirectory)
            {
                IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions { Title = setting.Label, AllowMultiple = false });

                if (folders.Count > 0)
                    setting.Value = folders[0].TryGetLocalPath() ?? folders[0].Path.ToString();

                return;
            }

            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = setting.Label,
                    AllowMultiple = false,
                    FileTypeFilter = ParseFilter(setting.Filter),
                });

            if (files.Count > 0)
                setting.Value = files[0].TryGetLocalPath() ?? files[0].Path.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            // A cancelled or unavailable picker leaves the typed value alone, which is the right
            // outcome: Browse is a convenience over a text box that is still there.
        }
    }

    /// <summary>
    /// Turns a <c>[SrtPath]</c> filter into what the picker wants.
    /// </summary>
    /// <remarks>
    /// The attribute's format is the familiar <c>Label|*.ext;*.ext</c> from the Win32 dialogs, which
    /// is what a plugin author coming from generation 1 will write. Null for anything that does not
    /// parse, so a malformed filter means "any file" rather than a picker that shows nothing.
    /// </remarks>
    private static IReadOnlyList<FilePickerFileType>? ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return null;

        List<FilePickerFileType> types = [];
        string[] parts = filter.Split('|');

        for (int index = 0; index + 1 < parts.Length; index += 2)
        {
            types.Add(new FilePickerFileType(parts[index])
            {
                Patterns = [.. parts[index + 1].Split(';', StringSplitOptions.RemoveEmptyEntries)],
            });
        }

        return types.Count > 0 ? types : null;
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: HotkeySettingViewModel setting } || !setting.Capturing)
            return;

        // A modifier on its own is half a shortcut. Swallowing it rather than recording it is what
        // lets somebody hold Ctrl and Shift before deciding which key they meant.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        List<string> parts = [];

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");

        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Win");

        // Escape cancels rather than being recorded: it is what a person presses to get out of a
        // capture they started by accident, and a shortcut of Escape is not worth losing that.
        if (e.Key != Key.Escape)
        {
            parts.Add(e.Key.ToString());
            setting.Capture(string.Join('+', parts));
        }
        else
        {
            setting.Capturing = false;
        }

        e.Handled = true;
    }
}
