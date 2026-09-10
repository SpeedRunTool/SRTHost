using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using SRTPluginBase.Abstractions;

namespace SpeedRunTool.Demo.ConsumerJson;

/// <summary>Settings for <see cref="JsonWriterConsumer"/>.</summary>
/// <remarks>
/// Deliberately small. <c>SpeedRunTool.Demo.Consumer</c> is the exhaustive tour of what the
/// generated form can draw; this one is what a real plugin's settings look like - four things the
/// user actually has a reason to change.
/// </remarks>
public sealed class JsonWriterConfiguration
{
    /// <summary>Where the files go. Empty means the plugin's own state directory.</summary>
    [SrtSetting(
        Order = 1,
        HelpText = "Leave empty to write into the plugin's state folder under %LOCALAPPDATA%\\SRTHost.")]
    [SrtPath(Directory = true)]
    [Display(Name = "Output folder")]
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>How long to wait between writes for a given channel.</summary>
    /// <remarks>
    /// A producer publishes about thirty times a second and this writes a file per payload, so the
    /// default throttle is the difference between a few writes a second and an unnecessary thirty.
    /// The last payload always wins; nothing is queued.
    /// </remarks>
    [SrtSetting(Order = 2, HelpText = "The newest payload wins; nothing is queued behind this.")]
    [Display(Name = "Write at most every")]
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether to re-indent the payload on the way out.</summary>
    /// <remarks>
    /// Off by default because it costs a parse and a re-serialise per write, and because writing the
    /// producer's own bytes through unaltered is the more honest default for a plugin that claims not
    /// to understand them.
    /// </remarks>
    [SrtSetting(Order = 3, HelpText = "Costs a parse and a re-serialise per write.")]
    [Display(Name = "Pretty-print")]
    public bool Indent { get; set; }

    /// <summary>Whether to wrap each payload with the frame's channel, sequence and codec.</summary>
    [SrtSetting(Order = 4)]
    [Display(Name = "Include frame metadata")]
    public bool IncludeFrameMetadata { get; set; } = true;
}

/// <summary>Source-generated serialisation for the settings.</summary>
[JsonSerializable(typeof(JsonWriterConfiguration))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
public partial class JsonWriterConfigurationJsonContext : JsonSerializerContext;
