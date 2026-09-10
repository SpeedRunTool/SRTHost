using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SpeedRunTool.Demo.Contracts;

namespace SpeedRunTool.Demo.ConsumerWindow;

/// <summary>
/// What the window shows. One property per row of the grid.
/// </summary>
/// <remarks>
/// <see cref="INotifyPropertyChanged"/> by hand, rather than with a source generator from an MVVM
/// package: six properties do not justify a plugin author taking a dependency, and this way the
/// example shows what the binding actually needs. Add CommunityToolkit.Mvvm (or ReactiveUI, or
/// nothing at all) if a real plugin's view model grows past this.
/// <para>
/// Every member here is touched on the UI thread only. The plugin marshals each payload across with
/// <see cref="AvaloniaUiThread.Post"/>, so there is no locking in a type that is updated thirty times
/// a second - which is the point of doing the marshalling rather than the synchronising.
/// </para>
/// </remarks>
public sealed class PayloadViewModel : INotifyPropertyChanged
{
    private string tick = "-";
    private string value = "-";
    private string ratio = "-";
    private string label = "-";
    private string status = "Waiting for the producer…";
    private long frames;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The payload's tick counter.</summary>
    public string Tick
    {
        get => tick;
        private set => Set(ref tick, value);
    }

    /// <summary>The payload's changing value.</summary>
    public string Value
    {
        get => value;
        private set => Set(ref this.value, value);
    }

    /// <summary>The payload's ratio, as a percentage.</summary>
    public string Ratio
    {
        get => ratio;
        private set => Set(ref ratio, value);
    }

    /// <summary>The payload's label.</summary>
    public string Label
    {
        get => label;
        private set => Set(ref label, value);
    }

    /// <summary>Whether data is arriving, and from where.</summary>
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    /// <summary>How many payloads have arrived, which is how you see at a glance that it is live.</summary>
    public long Frames
    {
        get => frames;
        private set => Set(ref frames, value);
    }

    /// <summary>Shows a payload.</summary>
    public void Update(DemoPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        Tick = payload.Tick.ToString(CultureInfo.CurrentCulture);
        Value = payload.Value.ToString(CultureInfo.CurrentCulture);
        Ratio = payload.Ratio.ToString("P0", CultureInfo.CurrentCulture);
        Label = payload.Label;
        Frames++;
        Status = "Receiving";
    }

    /// <summary>Clears the display: the producer has gone away.</summary>
    /// <remarks>
    /// Blanking the values rather than leaving them is the whole reason
    /// <c>IConsumerPlugin.OnChannelClosedAsync</c> exists. A HUD still showing the last frame of a
    /// closed game reads as live data and is worse than an empty one.
    /// </remarks>
    public void Clear(string channelId)
    {
        Tick = "-";
        Value = "-";
        Ratio = "-";
        Label = "-";
        Status = $"{channelId} closed";
    }

    private void Set<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, newValue))
            return;

        field = newValue;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
