using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRTHost.Core.Configuration;

namespace SRTHost.ViewModels;

/// <summary>
/// One control in a generated settings form.
/// </summary>
/// <remarks>
/// One type per editor, each rendered by a <c>DataTemplate</c> keyed on the type, so the view
/// contains no control construction and no <c>switch</c> over an editor enum. Adding an editor is
/// then a class and a template, and forgetting one shows up as an unrendered row rather than as a
/// silently wrong control.
/// <para>
/// A node never owns its value. The document is the single copy, held by
/// <see cref="PluginSettingsViewModel"/>: a node reads its starting value out of it and writes back
/// through <see cref="Commit"/>. Two representations of the same setting - one in a control, one in
/// the document - is exactly how a form comes to save something the user did not type.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public abstract partial class SettingNodeViewModel : ObservableObject
{
    private bool loading;

    /// <summary>Creates a node for one setting.</summary>
    protected SettingNodeViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(descriptor);

        Owner = owner;
        Descriptor = descriptor;
    }

    /// <summary>The page this node belongs to.</summary>
    protected PluginSettingsViewModel Owner { get; }

    /// <summary>What the schema and hints said about this setting.</summary>
    public SettingDescriptor Descriptor { get; }

    /// <summary>Where the value lives in the document.</summary>
    public string Path => Descriptor.Path;

    /// <summary>The form label.</summary>
    public string Label => Descriptor.Label;

    /// <summary>The help text under the control, if any.</summary>
    public string? Help => Descriptor.Help;

    /// <summary>Whether this setting hides behind "Show advanced".</summary>
    public bool Advanced => Descriptor.Advanced;

    /// <summary>
    /// Whether this node is a nested group rather than a single value.
    /// </summary>
    /// <remarks>
    /// A property rather than a type test in XAML, which has no way to express one. A group's label
    /// is its expander header, so the shared row template hides its own label for one.
    /// </remarks>
    public bool IsGroup => this is ObjectSettingViewModel;

    /// <summary>The problem with the current value, or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Whether the row is shown at all.</summary>
    /// <remarks>
    /// False when an unmet <c>[SrtDependsOn]</c> asked for hiding, or when the setting is advanced
    /// and advanced settings are not being shown.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>Whether the control accepts input.</summary>
    /// <remarks>
    /// Disabled rather than hidden is the default for an unmet dependency: it keeps the option
    /// discoverable, so a person can see that the thing they are looking for exists and what has to
    /// be true for it to apply.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>The value this control currently holds, in the document's representation.</summary>
    public abstract JsonNode? ToJson();

    /// <summary>Fills the control from <paramref name="value"/>, without writing anything back.</summary>
    public void Load(JsonNode? value)
    {
        loading = true;

        try
        {
            LoadCore(value);
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>Fills the control from the document.</summary>
    protected abstract void LoadCore(JsonNode? value);

    /// <summary>
    /// Writes this node's value into the document and revalidates the form.
    /// </summary>
    /// <remarks>
    /// Ignored while <see cref="Load"/> is running. Every observable property here raises a change
    /// notification when it is filled in, and without this guard loading a document would look
    /// exactly like a person editing every field in it - which would mark a freshly opened page
    /// dirty and re-evaluate every dependency for nothing.
    /// </remarks>
    protected void Commit()
    {
        if (!loading)
            Owner.ValueChanged(this);
    }

    /// <summary>Restores the value a fresh instance of the settings type would have.</summary>
    [RelayCommand]
    public void Reset()
    {
        Load(Descriptor.Default);
        Owner.ValueChanged(this);
    }
}

/// <summary>A boolean, as a switch.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class BooleanSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    : SettingNodeViewModel(owner, descriptor)
{
    /// <summary>Whether the setting is on.</summary>
    [ObservableProperty]
    public partial bool Value { get; set; }

    /// <inheritdoc />
    public override JsonNode? ToJson() => JsonValue.Create(Value);

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value) => Value = value?.GetValueKind() == JsonValueKind.True;

    partial void OnValueChanged(bool value) => Commit();
}

/// <summary>A fixed set of choices, as a drop-down.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class EnumSettingViewModel : SettingNodeViewModel
{
    /// <summary>Creates the drop-down over the options the runner listed.</summary>
    public EnumSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
        : base(owner, descriptor)
    {
        Options = [.. descriptor.Options];
    }

    /// <summary>The choices, in the order the enum declares them.</summary>
    public IReadOnlyList<SettingOption> Options { get; }

    /// <summary>The chosen option.</summary>
    [ObservableProperty]
    public partial SettingOption? Selected { get; set; }

    /// <inheritdoc />
    public override JsonNode? ToJson() => Selected?.Value?.DeepClone();

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
        => Selected = Options.FirstOrDefault(option => JsonNode.DeepEquals(option.Value, value))
            ?? Options.FirstOrDefault(option => JsonNode.DeepEquals(option.Value, Descriptor.Default));

    partial void OnSelectedChanged(SettingOption? value) => Commit();
}

/// <summary>One member of a <c>[Flags]</c> enum, as a checkbox.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class FlagOptionViewModel(FlagsSettingViewModel owner, long bit, string label) : ObservableObject
{
    /// <summary>The bit this box sets.</summary>
    public long Bit { get; } = bit;

    /// <summary>What the user sees.</summary>
    public string Label { get; } = label;

    /// <summary>Whether the bit is set.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => owner.FlagToggled();
}

/// <summary>A combination of flags, as a list of checkboxes.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class FlagsSettingViewModel : SettingNodeViewModel
{
    private bool loadingFlags;

    /// <summary>Creates one checkbox per single-bit member.</summary>
    public FlagsSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
        : base(owner, descriptor)
    {
        List<FlagOptionViewModel> flags = [];

        foreach (SettingOption option in descriptor.Options)
        {
            long bit = ToInt64(option.Value);

            // Zero is the enum's None, and a member with more than one bit set is a named
            // combination of others. Neither is a checkbox: ticking "All" and then unticking one of
            // its members has no coherent meaning, and showing "None" as something to tick alongside
            // the values it excludes is worse than leaving it out.
            if (bit != 0 && (bit & (bit - 1)) == 0)
                flags.Add(new FlagOptionViewModel(this, bit, option.Label));
        }

        Flags = flags;
    }

    /// <summary>The single-bit members.</summary>
    public IReadOnlyList<FlagOptionViewModel> Flags { get; }

    /// <inheritdoc />
    public override JsonNode? ToJson()
    {
        long value = 0;

        foreach (FlagOptionViewModel flag in Flags)
        {
            if (flag.IsChecked)
                value |= flag.Bit;
        }

        return JsonValue.Create(value);
    }

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
    {
        long current = ToInt64(value ?? Descriptor.Default);

        loadingFlags = true;

        try
        {
            foreach (FlagOptionViewModel flag in Flags)
                flag.IsChecked = (current & flag.Bit) == flag.Bit;
        }
        finally
        {
            loadingFlags = false;
        }
    }

    /// <summary>Called by a checkbox when the user changes it.</summary>
    public void FlagToggled()
    {
        if (!loadingFlags)
            Commit();
    }

    private static long ToInt64(JsonNode? value) => SettingNumber.TryRead(value, out long parsed) ? parsed : 0;
}

/// <summary>A number, as a spinner and - when it has a declared range - a slider.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class NumberSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    : SettingNodeViewModel(owner, descriptor)
{
    /// <summary>The value.</summary>
    [ObservableProperty]
    public partial decimal Value { get; set; }

    /// <summary>Lowest accepted value, or the spinner's floor when there is no range.</summary>
    public decimal Minimum { get; } = ToDecimal(descriptor.Minimum) ?? decimal.MinValue;

    /// <summary>Highest accepted value, or the spinner's ceiling when there is no range.</summary>
    public decimal Maximum { get; } = ToDecimal(descriptor.Maximum) ?? decimal.MaxValue;

    /// <summary>Spinner and slider increment.</summary>
    public decimal Step { get; } = ToDecimal(descriptor.Step) ?? (descriptor.Integral ? 1m : 0.01m);

    /// <summary>Whether to show a slider beside the spinner.</summary>
    public bool ShowSlider => Descriptor.Editor == SettingEditor.Slider;

    /// <summary>How the spinner formats the value.</summary>
    public string FormatString => Descriptor.Integral ? "F0" : "F2";

    /// <summary>The value as a double, which is what a <c>Slider</c> binds to.</summary>
    /// <remarks>
    /// The spinner works in <see cref="decimal"/> and the slider in <see cref="double"/>, so one of
    /// them has to convert. Doing it here, against the same backing property, keeps the two controls
    /// showing the same number - binding them to separate properties is how a slider and a spinner
    /// come to disagree.
    /// </remarks>
    public double SliderValue
    {
        get => (double)Value;
        set => Value = ToDecimal(value) ?? Value;
    }

    /// <summary>Lowest accepted value, as a double for the slider.</summary>
    public double SliderMinimum => (double)Minimum;

    /// <summary>Highest accepted value, as a double for the slider.</summary>
    public double SliderMaximum => (double)Maximum;

    /// <summary>Slider increment.</summary>
    public double SliderStep => (double)Step;

    /// <inheritdoc />
    public override JsonNode? ToJson()
        => Descriptor.Integral ? JsonValue.Create((long)Value) : JsonValue.Create((double)Value);

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
    {
        JsonNode? source = value?.GetValueKind() == JsonValueKind.Number ? value : Descriptor.Default;

        Value = SettingNumber.TryRead(source, out double parsed) ? ToDecimal(parsed) ?? 0m : 0m;
    }

    partial void OnValueChanged(decimal value)
    {
        OnPropertyChanged(nameof(SliderValue));
        Commit();
    }

    private static decimal? ToDecimal(double? value)
    {
        if (value is not { } number || double.IsNaN(number) || double.IsInfinity(number))
            return null;

        // [Range(int.MinValue, int.MaxValue)] is ordinary, and so is a double range wider than
        // decimal can hold. Clamping rather than throwing keeps an over-wide range from taking the
        // whole settings page down with it.
        return number switch
        {
            <= (double)decimal.MinValue => decimal.MinValue,
            >= (double)decimal.MaxValue => decimal.MaxValue,
            _ => (decimal)number,
        };
    }
}

/// <summary>A string, as a text box - optionally multi-line, or with a Browse button.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class TextSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    : SettingNodeViewModel(owner, descriptor)
{
    /// <summary>The text.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>Whether the box accepts newlines.</summary>
    public bool Multiline => Descriptor.Editor == SettingEditor.Multiline;

    /// <summary>Whether to offer a Browse button beside it.</summary>
    public bool CanBrowse => Descriptor.Editor == SettingEditor.Path;

    /// <summary>Whether Browse picks a folder rather than a file.</summary>
    public bool PickDirectory => Descriptor.PickDirectory;

    /// <summary>The file filter, in <c>Label|*.ext</c> form.</summary>
    public string? Filter => Descriptor.Filter;

    /// <summary>How tall the box starts, so a multi-line one is visibly multi-line.</summary>
    public double EditorMinHeight => Multiline ? 96 : 0;

    /// <inheritdoc />
    public override JsonNode? ToJson() => JsonValue.Create(Value);

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
        => Value = value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;

    partial void OnValueChanged(string value) => Commit();
}

/// <summary>A colour, as a hex string with a live swatch.</summary>
/// <remarks>
/// A text box and a preview rather than Avalonia's <c>ColorPicker</c>, which lives in a package this
/// project does not otherwise need. The swatch is what makes the text box usable - it turns "did I
/// get that hex right" into something you can see - and moving to the full picker later is a
/// template change with the same view model behind it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class ColorSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    : SettingNodeViewModel(owner, descriptor)
{
    /// <summary>The colour, as <c>#AARRGGBB</c> or <c>#RRGGBB</c>.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>Whether the plugin stores this as a packed integer rather than a string.</summary>
    public bool Packed => Descriptor.Editor == SettingEditor.Color && Descriptor.Integral;

    /// <inheritdoc />
    public override JsonNode? ToJson()
    {
        if (!Packed)
            return JsonValue.Create(Value);

        string text = Value.TrimStart('#');

        return JsonValue.Create(
            long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long packed) ? packed : 0L);
    }

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
    {
        JsonNode? source = value ?? Descriptor.Default;

        Value = source?.GetValueKind() switch
        {
            JsonValueKind.String => source.GetValue<string>(),
            JsonValueKind.Number when SettingNumber.TryRead(source, out long packed)
                => "#" + packed.ToString("X8", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    partial void OnValueChanged(string value) => Commit();
}

/// <summary>A font, as a drop-down over what is installed.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class FontSettingViewModel : SettingNodeViewModel
{
    /// <summary>Creates the drop-down from the system's font list.</summary>
    public FontSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor, IReadOnlyList<string> fonts)
        : base(owner, descriptor)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        Fonts = fonts;
    }

    /// <summary>Every font family on the machine, sorted.</summary>
    public IReadOnlyList<string> Fonts { get; }

    /// <summary>The chosen family.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>
    /// What the drop-down binds to: the value when it is a font this machine has, and null when it
    /// is not.
    /// </summary>
    /// <remarks>
    /// The indirection is what stops a settings file naming a font that is not installed here from
    /// being silently rewritten. Binding <see cref="Value"/> straight to <c>SelectedItem</c> would
    /// let the <c>ComboBox</c> push null back the moment it could not find the item, which is a
    /// settings page quietly destroying a setting because it was opened on the wrong machine.
    /// </remarks>
    public string? SelectedFont
    {
        get => Fonts.Contains(Value, StringComparer.CurrentCultureIgnoreCase) ? Value : null;
        set
        {
            if (value is not null)
                Value = value;
        }
    }

    /// <inheritdoc />
    public override JsonNode? ToJson() => JsonValue.Create(Value);

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
        => Value = value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedFont));
        Commit();
    }
}

/// <summary>A shortcut, recorded by pressing it.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class HotkeySettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    : SettingNodeViewModel(owner, descriptor)
{
    /// <summary>The shortcut, as <c>Ctrl+Shift+F7</c>.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>Whether the next key press is being recorded.</summary>
    [ObservableProperty]
    public partial bool Capturing { get; set; }

    /// <summary>What the button says.</summary>
    public string Prompt => Capturing
        ? "Press a key combination…"
        : Value.Length > 0 ? Value : "Not set";

    /// <summary>Records a combination the view captured.</summary>
    public void Capture(string gesture)
    {
        Value = gesture;
        Capturing = false;
    }

    /// <summary>Clears the shortcut.</summary>
    [RelayCommand]
    private void Clear()
    {
        Value = string.Empty;
        Capturing = false;
    }

    /// <inheritdoc />
    public override JsonNode? ToJson() => JsonValue.Create(Value);

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
        => Value = value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(Prompt));
        Commit();
    }

    partial void OnCapturingChanged(bool value) => OnPropertyChanged(nameof(Prompt));
}

/// <summary>A duration, as three spinners over an <c>hh:mm:ss</c> string.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class DurationSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    : SettingNodeViewModel(owner, descriptor)
{
    /// <summary>Whole hours.</summary>
    [ObservableProperty]
    public partial int Hours { get; set; }

    /// <summary>Minutes past the hour.</summary>
    [ObservableProperty]
    public partial int Minutes { get; set; }

    /// <summary>Seconds past the minute.</summary>
    [ObservableProperty]
    public partial int Seconds { get; set; }

    /// <inheritdoc />
    public override JsonNode? ToJson()
        => JsonValue.Create(new TimeSpan(Hours, Minutes, Seconds).ToString("c", CultureInfo.InvariantCulture));

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
    {
        JsonNode? source = value?.GetValueKind() == JsonValueKind.String ? value : Descriptor.Default;

        TimeSpan duration = source?.GetValueKind() == JsonValueKind.String
            && TimeSpan.TryParse(source.GetValue<string>(), CultureInfo.InvariantCulture, out TimeSpan parsed)
                ? parsed
                : TimeSpan.Zero;

        Hours = (int)duration.TotalHours;
        Minutes = duration.Minutes;
        Seconds = duration.Seconds;
    }

    partial void OnHoursChanged(int value) => Commit();

    partial void OnMinutesChanged(int value) => Commit();

    partial void OnSecondsChanged(int value) => Commit();
}

/// <summary>One entry in a list setting.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ListItemViewModel(ListSettingViewModel owner, string text) : ObservableObject
{
    /// <summary>The entry, as text; it is parsed to the element type on save.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = text;

    /// <summary>Removes this entry.</summary>
    [RelayCommand]
    private void Remove() => owner.Remove(this);

    partial void OnTextChanged(string value) => owner.ItemChanged();
}

/// <summary>A list of primitives, as an editable list with Add and Remove.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ListSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    : SettingNodeViewModel(owner, descriptor)
{
    private bool loadingItems;

    /// <summary>The entries.</summary>
    public ObservableCollection<ListItemViewModel> Items { get; } = [];

    /// <inheritdoc />
    public override JsonNode? ToJson()
    {
        JsonArray array = [];

        foreach (ListItemViewModel item in Items)
            array.Add(Parse(item.Text));

        return array;
    }

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
    {
        loadingItems = true;

        try
        {
            Items.Clear();

            if ((value ?? Descriptor.Default) is JsonArray array)
            {
                foreach (JsonNode? element in array)
                    Items.Add(new ListItemViewModel(this, element?.ToString() ?? string.Empty));
            }
        }
        finally
        {
            loadingItems = false;
        }
    }

    /// <summary>Adds an empty entry for the user to fill in.</summary>
    [RelayCommand]
    private void Add()
    {
        Items.Add(new ListItemViewModel(this, string.Empty));
        Commit();
    }

    /// <summary>Removes an entry.</summary>
    public void Remove(ListItemViewModel item)
    {
        Items.Remove(item);
        Commit();
    }

    /// <summary>Called by an entry when its text changes.</summary>
    public void ItemChanged()
    {
        if (!loadingItems)
            Commit();
    }

    private JsonNode? Parse(string text) => Descriptor.ItemEditor switch
    {
        SettingEditor.Toggle => JsonValue.Create(bool.TryParse(text, out bool flag) && flag),
        SettingEditor.Number when long.TryParse(text, CultureInfo.InvariantCulture, out long integer)
            => JsonValue.Create(integer),
        SettingEditor.Number when double.TryParse(text, CultureInfo.InvariantCulture, out double number)
            => JsonValue.Create(number),

        // A number the user has not finished typing is kept as a string rather than discarded. The
        // runner rejects the document and says so, which beats silently losing the entry - and the
        // half-typed value is still on screen to be corrected.
        _ => JsonValue.Create(text),
    };
}

/// <summary>A nested group of settings, as an expander.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ObjectSettingViewModel : SettingNodeViewModel
{
    /// <summary>Creates the group and its children.</summary>
    public ObjectSettingViewModel(
        PluginSettingsViewModel owner,
        SettingDescriptor descriptor,
        IReadOnlyList<SettingNodeViewModel> children)
        : base(owner, descriptor)
    {
        ArgumentNullException.ThrowIfNull(children);
        Children = children;
    }

    /// <summary>The settings inside the group.</summary>
    public IReadOnlyList<SettingNodeViewModel> Children { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Null, and never called: the children write themselves into the document at their own paths,
    /// so writing the group as a whole would overwrite their work with a second, stale copy.
    /// </remarks>
    public override JsonNode? ToJson() => null;

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
    {
        JsonObject? nested = value as JsonObject;

        foreach (SettingNodeViewModel child in Children)
            child.Load(nested?[child.Descriptor.Name]);
    }
}

/// <summary>
/// A property the host cannot draw, as a raw JSON box.
/// </summary>
/// <remarks>
/// The per-property half of the documented fallback, and the reason the declarative layer can never
/// fully block a plugin author: a type nothing here understands still gets an editable row, and
/// everything around it still gets a proper control. The whole-document editor on the JSON tab is
/// the other half.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class JsonSettingViewModel(PluginSettingsViewModel owner, SettingDescriptor descriptor)
    : SettingNodeViewModel(owner, descriptor)
{
    private JsonNode? parsed;

    /// <summary>The value, as JSON text.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <inheritdoc />
    public override JsonNode? ToJson() => parsed?.DeepClone();

    /// <inheritdoc />
    protected override void LoadCore(JsonNode? value)
    {
        parsed = (value ?? Descriptor.Default)?.DeepClone();
        Value = parsed?.ToJsonString(Indented) ?? "null";
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    partial void OnValueChanged(string value)
    {
        try
        {
            parsed = JsonNode.Parse(value);
            Error = null;
        }
        catch (JsonException ex)
        {
            // Kept out of the document until it parses: half-typed JSON must not be what gets sent
            // when the user presses Save on an unrelated setting.
            Error = ex.Message;
            return;
        }

        Commit();
    }
}
