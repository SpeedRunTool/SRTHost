using System.Text.Json.Nodes;

namespace SRTHost.Core.Configuration;

/// <summary>
/// Which control a setting is edited with.
/// </summary>
/// <remarks>
/// Chosen by the runner, which is the only process that can see the plugin's types, and carried in
/// the hints sidecar rather than inferred from the schema. The host does not second-guess it: a
/// value it does not recognise becomes <see cref="Json"/>, which is always editable.
/// </remarks>
public enum SettingEditor
{
    /// <summary>Raw JSON for one property - the fallback that is never wrong, only inconvenient.</summary>
    Json,

    /// <summary>A switch, for a boolean.</summary>
    Toggle,

    /// <summary>A drop-down over a fixed set of values.</summary>
    Enum,

    /// <summary>A list of checkboxes over the members of a <c>[Flags]</c> enum.</summary>
    Flags,

    /// <summary>A slider paired with a spinner, for a number with a declared range.</summary>
    Slider,

    /// <summary>A spinner, for a number without one.</summary>
    Number,

    /// <summary>A single-line text box.</summary>
    Text,

    /// <summary>A multi-line text box.</summary>
    Multiline,

    /// <summary>A text box with a Browse button.</summary>
    Path,

    /// <summary>A colour picker.</summary>
    Color,

    /// <summary>A drop-down over the fonts installed on the machine.</summary>
    Font,

    /// <summary>A button that records the next key combination pressed.</summary>
    Hotkey,

    /// <summary>A duration editor over an <c>hh:mm:ss</c> string.</summary>
    Duration,

    /// <summary>An editable list of primitives.</summary>
    List,

    /// <summary>A nested group of settings, rendered as an expander.</summary>
    Object,
}

/// <summary>One choice in an enum or flags setting.</summary>
/// <param name="Value">The value as it is written to the settings document.</param>
/// <param name="Label">What the user sees.</param>
public sealed record SettingOption(JsonNode? Value, string Label);

/// <summary>
/// A condition under which a setting applies.
/// </summary>
/// <param name="PropertyPath">The sibling setting to test, as a path from the document root.</param>
/// <param name="Value">The value it has to equal.</param>
/// <param name="HideWhenUnmet">Hide the setting rather than disabling it when the test fails.</param>
public sealed record SettingDependency(string PropertyPath, JsonNode? Value, bool HideWhenUnmet);

/// <summary>
/// One editable setting, as the host understands it.
/// </summary>
/// <remarks>
/// The whole of what the schema and hints said about one property, flattened into something a view
/// model can consume without parsing JSON again. It is deliberately a plain record with no Avalonia
/// in sight, so the parsing and grouping rules are testable without a rendering stack - which is
/// where the interesting mistakes are.
/// </remarks>
public sealed record SettingDescriptor
{
    /// <summary>Where the value lives in the document, as <c>Parent/Child</c>.</summary>
    /// <remarks>
    /// The segments are the exact JSON keys the plugin's own serialiser uses, because the runner
    /// took them from its <c>JsonTypeInfo</c> rather than from the CLR property names.
    /// </remarks>
    public required string Path { get; init; }

    /// <summary>The last segment of <see cref="Path"/> - the key within its own object.</summary>
    public required string Name { get; init; }

    /// <summary>What the form calls it.</summary>
    public required string Label { get; init; }

    /// <summary>Longer explanation, shown under the control.</summary>
    public string? Help { get; init; }

    /// <summary>Which group heading it files under, or null for the unnamed first group.</summary>
    public string? Group { get; init; }

    /// <summary>Sort order within the group. Ties keep declaration order.</summary>
    public int Order { get; init; }

    /// <summary>Whether it hides behind "Show advanced".</summary>
    public bool Advanced { get; init; }

    /// <summary>Which control edits it.</summary>
    public SettingEditor Editor { get; init; } = SettingEditor.Json;

    /// <summary>Whether the value must be present and non-empty.</summary>
    public bool Required { get; init; }

    /// <summary>The value a fresh instance of the settings type has, for Reset.</summary>
    public JsonNode? Default { get; init; }

    /// <summary>Lowest accepted value, from <c>[Range]</c>.</summary>
    public double? Minimum { get; init; }

    /// <summary>Highest accepted value, from <c>[Range]</c>.</summary>
    public double? Maximum { get; init; }

    /// <summary>Slider and spinner increment.</summary>
    public double? Step { get; init; }

    /// <summary>Whether the number is integral, so the editor does not offer fractions.</summary>
    public bool Integral { get; init; }

    /// <summary>Shortest accepted string.</summary>
    public int? MinimumLength { get; init; }

    /// <summary>Longest accepted string.</summary>
    public int? MaximumLength { get; init; }

    /// <summary>Regular expression the string has to match.</summary>
    public string? Pattern { get; init; }

    /// <summary>Whether a path setting picks a directory rather than a file.</summary>
    public bool PickDirectory { get; init; }

    /// <summary>File filter for a path setting, in <c>Label|*.ext</c> form.</summary>
    public string? Filter { get; init; }

    /// <summary>The choices, for an enum or flags setting.</summary>
    public IReadOnlyList<SettingOption> Options { get; init; } = [];

    /// <summary>What has to be true elsewhere for this setting to apply.</summary>
    public IReadOnlyList<SettingDependency> DependsOn { get; init; } = [];

    /// <summary>How one element of a list setting is edited.</summary>
    public SettingEditor ItemEditor { get; init; } = SettingEditor.Text;

    /// <summary>The settings inside a nested object.</summary>
    public IReadOnlyList<SettingDescriptor> Children { get; init; } = [];
}

/// <summary>A heading and the settings under it.</summary>
/// <param name="Name">The heading, or an empty string for the settings declared with no group.</param>
/// <param name="Settings">Its settings, already ordered.</param>
public sealed record SettingGroup(string Name, IReadOnlyList<SettingDescriptor> Settings);
