using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace SRTHost.Views;

/// <summary>
/// Turns the severity key a row reports into the brush the status dot is painted with.
/// </summary>
/// <remarks>
/// The indirection exists so the view models never reference an Avalonia type: a view model that
/// returns a <see cref="IBrush"/> cannot be unit tested without a rendering stack, and the palette
/// ends up scattered across whichever files happened to need a colour. The keys live in App.axaml.
/// </remarks>
public sealed class SeverityBrushConverter : IValueConverter
{
    /// <summary>The shared instance the XAML resource points at.</summary>
    public static SeverityBrushConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = (value as string) switch
        {
            "ok" => "SeverityOk",
            "busy" => "SeverityBusy",
            "idle" => "SeverityIdle",
            "warning" => "SeverityWarning",
            _ => "SeverityError",
        };

        return Application.Current?.TryGetResource(key, ThemeVariant.Default, out object? brush) == true && brush is IBrush found
            ? found
            : Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("A status dot is never edited.");
}

/// <summary>Turns a producer's source-availability flag into words.</summary>
/// <remarks>
/// "Available" and "not available" rather than a checkbox, because for a producer this is "is the
/// game running" - the first thing anybody checks when an overlay is blank, and the last thing that
/// should be encoded as an unlabelled tick.
/// </remarks>
public sealed class SourceAvailabilityConverter : IValueConverter
{
    /// <summary>The shared instance the XAML resource points at.</summary>
    public static SourceAvailabilityConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Available" : "Not available";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Source availability is reported by the plugin, not set here.");
}
