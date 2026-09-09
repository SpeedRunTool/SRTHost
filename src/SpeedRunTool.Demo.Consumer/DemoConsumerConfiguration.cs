using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using SRTPluginBase.Abstractions;

namespace SpeedRunTool.Demo.Consumer;

/// <summary>Where an overlay sits on the screen.</summary>
public enum DemoCorner
{
    /// <summary>Top left.</summary>
    [Display(Name = "Top left")]
    TopLeft,

    /// <summary>Top right.</summary>
    [Display(Name = "Top right")]
    TopRight,

    /// <summary>Bottom left.</summary>
    [Display(Name = "Bottom left")]
    BottomLeft,

    /// <summary>Bottom right.</summary>
    [Display(Name = "Bottom right")]
    BottomRight,
}

/// <summary>Which parts of the payload to show.</summary>
[Flags]
public enum DemoFields
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The tick counter.</summary>
    Tick = 1 << 0,

    /// <summary>The changing value.</summary>
    Value = 1 << 1,

    /// <summary>The ratio.</summary>
    Ratio = 1 << 2,

    /// <summary>The label.</summary>
    Label = 1 << 3,

    /// <summary>Everything.</summary>
    All = Tick | Value | Ratio | Label,
}

/// <summary>Where the log line goes and how it looks.</summary>
public sealed class DemoAppearance
{
    /// <summary>Text colour.</summary>
    [SrtColor]
    [Display(Name = "Text colour")]
    public string Foreground { get; set; } = "#FF20C020";

    /// <summary>Which font to draw in.</summary>
    [SrtFont]
    [Display(Name = "Font")]
    public string Font { get; set; } = "Consolas";

    /// <summary>How large, in points.</summary>
    [Range(6, 72)]
    [Display(Name = "Font size")]
    public int FontSize { get; set; } = 14;
}

/// <summary>
/// Settings for <see cref="DemoConsumer"/>, and the reference for what a settings type can look
/// like.
/// </summary>
/// <remarks>
/// Deliberately covers every editor the host can generate - a switch, a drop-down, checkboxes for a
/// <c>[Flags]</c> enum, a slider, a spinner, text, a path, a colour, a font, a shortcut, a duration,
/// a list and a nested group - plus <see cref="Ignored"/>, which is a type the host cannot draw and
/// which must therefore degrade to a JSON row without taking the rest of the form with it. That last
/// one is the whole reason the fallback exists, so the demo carries it on purpose.
/// <para>
/// It is also the shape a plugin author copies: plain properties with <c>[Display]</c> for labels,
/// <c>DataAnnotations</c> for validation, and the <c>Srt*</c> attributes only where DataAnnotations
/// has nothing to say.
/// </para>
/// </remarks>
public sealed class DemoConsumerConfiguration
{
    /// <summary>Whether to log payloads at all.</summary>
    [SrtSetting(Group = "General", Order = 1, HelpText = "Turn off to keep the plugin running quietly.")]
    [Display(Name = "Log payloads")]
    public bool Enabled { get; set; } = true;

    /// <summary>Which corner an overlay would sit in.</summary>
    [SrtSetting(Group = "General", Order = 2)]
    [SrtDependsOn(nameof(Enabled), true)]
    [Display(Name = "Corner")]
    public DemoCorner Corner { get; set; } = DemoCorner.TopLeft;

    /// <summary>Which fields to include.</summary>
    [SrtSetting(Group = "General", Order = 3)]
    [SrtDependsOn(nameof(Enabled), true)]
    [Display(Name = "Fields to show")]
    public DemoFields Fields { get; set; } = DemoFields.All;

    /// <summary>How opaque, from invisible to solid.</summary>
    [SrtSetting(Group = "General", Order = 4)]
    [Range(0.0, 1.0)]
    [Display(Name = "Opacity")]
    public double Opacity { get; set; } = 0.85;

    /// <summary>Log at most one line in this many payloads.</summary>
    [SrtSetting(Group = "General", Order = 5, HelpText = "1 logs every payload; 30 logs about once a second.")]
    [Range(1, 600)]
    [Display(Name = "Log every N payloads")]
    public int LogEvery { get; set; } = 30;

    /// <summary>Text put in front of every line.</summary>
    [SrtSetting(Group = "General", Order = 6)]
    [StringLength(32)]
    [Display(Name = "Line prefix")]
    public string Prefix { get; set; } = "demo";

    /// <summary>How the line is drawn.</summary>
    [SrtSetting(Group = "Appearance", Order = 1)]
    [Display(Name = "Appearance")]
    public DemoAppearance Appearance { get; set; } = new();

    /// <summary>Where to also write the payloads, if anywhere.</summary>
    [SrtSetting(Group = "Advanced", Order = 1, Advanced = true)]
    [SrtPath(Directory = true)]
    [Display(Name = "Also write to folder")]
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>A shortcut that would toggle the overlay.</summary>
    [SrtSetting(Group = "Advanced", Order = 2, Advanced = true)]
    [SrtHotkey]
    [Display(Name = "Toggle shortcut")]
    public string ToggleHotkey { get; set; } = "Ctrl+F7";

    /// <summary>How long to keep showing the last payload after the producer goes away.</summary>
    [SrtSetting(Group = "Advanced", Order = 3, Advanced = true)]
    [Display(Name = "Keep last payload for")]
    public TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Labels to ignore.</summary>
    [SrtSetting(Group = "Advanced", Order = 4, Advanced = true)]
    [Display(Name = "Ignore these labels")]
    public List<string> IgnoredLabels { get; set; } = [];

    /// <summary>
    /// A type the generated form has no control for, kept here on purpose.
    /// </summary>
    /// <remarks>
    /// A dictionary is perfectly serialisable and completely undrawable as a labelled control, which
    /// makes it the exact case the per-property JSON fallback exists for. The rest of the form has
    /// to keep working around it - that is the P6 checkpoint, and this property is what proves it.
    /// </remarks>
    [SrtSetting(Group = "Advanced", Order = 5, Advanced = true, HelpText = "No control fits this, so it is edited as JSON.")]
    [Display(Name = "Label replacements")]
    public Dictionary<string, string> Ignored { get; set; } = [];

    /// <summary>Never shown: <see cref="SrtHiddenAttribute"/> keeps it out of the form entirely.</summary>
    [SrtHidden]
    public int InternalRevision { get; set; }
}

/// <summary>
/// Source-generated serialisation for the settings.
/// </summary>
/// <remarks>
/// <see cref="DemoCorner"/> is written by name and <see cref="DemoFields"/> as a number, which is
/// deliberate rather than an oversight: a hand-edited file saying <c>"Corner": "BottomRight"</c> is
/// worth far more than one saying <c>3</c>, while a combination of flags reads no better as
/// <c>"Tick, Ratio"</c> than as a number and is far easier to get wrong by hand.
/// <para>
/// The point for a plugin author is that neither choice needs telling to the host. The generated
/// form follows whichever representation this context actually produces - the runner reads a
/// default straight out of it - so a settings type can use string enums, numeric enums or a custom
/// converter and the form writes back something the plugin will read.
/// </para>
/// </remarks>
[JsonSerializable(typeof(DemoConsumerConfiguration))]
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    Converters = [typeof(JsonStringEnumConverter<DemoCorner>)])]
public partial class DemoConsumerConfigurationJsonContext : JsonSerializerContext;
