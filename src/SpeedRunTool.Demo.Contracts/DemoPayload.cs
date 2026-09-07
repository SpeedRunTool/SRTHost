using System.Text.Json.Serialization;

namespace SpeedRunTool.Demo.Contracts;

/// <summary>Channel constants for the demo producer.</summary>
public static class DemoChannel
{
    /// <summary>The channel id consumers subscribe to.</summary>
    public const string Id = "srt/demo/values";

    /// <summary>The payload contract version published on this channel.</summary>
    public static Version ContractVersion { get; } = new(1, 0);
}

/// <summary>A synthetic payload standing in for game memory.</summary>
public sealed class DemoPayload
{
    /// <summary>Monotonic counter, so a consumer can see updates arriving.</summary>
    public long Tick { get; set; }

    /// <summary>An arbitrary changing value.</summary>
    public int Value { get; set; }

    /// <summary>A ratio in the range 0 to 1, to exercise a bounded numeric setting.</summary>
    public float Ratio { get; set; }

    /// <summary>An arbitrary label.</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Source-generated serialisation for <see cref="DemoPayload"/>. Producers and consumers both use
/// this, so encoding and decoding cannot drift, and neither pays reflection cost per tick.
/// </summary>
[JsonSerializable(typeof(DemoPayload))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
public partial class DemoPayloadJsonContext : JsonSerializerContext;
