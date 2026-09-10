using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using SRTPluginBase.Abstractions;

[assembly: SrtPluginAssembly(
    "SpeedRunTool.Demo.ConsumerJson",
    typeof(SpeedRunTool.Demo.ConsumerJson.JsonWriterConsumer))]

namespace SpeedRunTool.Demo.ConsumerJson;

/// <summary>
/// Writes the newest payload of every channel to a file, without knowing what any of them contain.
/// </summary>
/// <remarks>
/// The schemaless consumer, and the second half of what the contracts split is for. It references no
/// producer and no payload-contract assembly: it subscribes to <c>*</c>, takes
/// <see cref="PayloadFrame.Payload"/> as the opaque bytes the wire actually carries, and writes them
/// out. A producer written years from now works with it unchanged.
/// <para>
/// It is also the example of composing the contract by hand rather than taking a base class. There is
/// no <c>ConsumerPluginBase</c> without a payload type - it exists to deserialise for you, which is
/// exactly what this plugin must not do - so this derives from
/// <see cref="ConfigurablePluginBase{TConfiguration}"/> for the settings half and implements
/// <see cref="IConsumerPlugin"/> itself. The base classes are conveniences; the interfaces are the
/// contract, and the host only ever looks for the interfaces.
/// </para>
/// </remarks>
public sealed class JsonWriterConsumer
    : ConfigurablePluginBase<JsonWriterConfiguration>, IConsumerPlugin
{
    private static readonly JsonWriterOptions IndentedOptions = new() { Indented = true };

    /// <summary>The last write per channel, so the throttle is per channel rather than global.</summary>
    private readonly Dictionary<string, long> lastWriteTimestamp = new(StringComparer.OrdinalIgnoreCase);

    private string outputFolder = string.Empty;

    /// <inheritdoc />
    public override IPluginInfo Info { get; } =
        PluginInfo.FromAssembly(typeof(JsonWriterConsumer).Assembly);

    /// <inheritdoc />
    /// <remarks>
    /// <c>*</c> is every channel, present and future, and <see cref="ChannelSubscription.Required"/>
    /// is false because this plugin is not useless without a particular producer - it has nothing in
    /// particular to wait for. (The host does not currently hold a consumer stopped for an unmet
    /// required subscription; declaring the truth here costs nothing and will be right when it does.)
    /// </remarks>
    public IReadOnlyList<ChannelSubscription> Subscriptions { get; } =
    [
        new ChannelSubscription { ChannelId = "*", Required = false },
    ];

    /// <inheritdoc />
    protected override JsonTypeInfo<JsonWriterConfiguration> ConfigurationTypeInfo
        => JsonWriterConfigurationJsonContext.Default.JsonWriterConfiguration;

    /// <inheritdoc />
    /// <remarks>
    /// Resolving the folder here rather than per payload is the pattern to copy: settings change a
    /// few times in a session, payloads arrive thirty times a second.
    /// </remarks>
    public override ValueTask OnConfigurationChangedAsync(
        JsonWriterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // StateDirectory, not PluginDirectory: an in-app update replaces the plugin's own folder
        // wholesale, so anything written there is lost.
        string resolved = string.IsNullOrWhiteSpace(configuration.OutputFolder)
            ? Path.Combine(Context.StateDirectory, "channels")
            : configuration.OutputFolder;

        // Throwing here rejects the settings: the host surfaces the message against the form and
        // does not write the document to disk, so an unusable path cannot come back at next start.
        try
        {
            Directory.CreateDirectory(resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException($"Cannot write to '{resolved}': {ex.Message}", ex);
        }

        outputFolder = resolved;
        lastWriteTimestamp.Clear();

        Logger.LogInformation("Writing channel payloads to {OutputFolder}.", outputFolder);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask ConsumeAsync(PayloadFrame frame, CancellationToken cancellationToken)
    {
        JsonWriterConfiguration settings = Configuration;

        if (!ShouldWrite(frame.ChannelId, settings.MinimumInterval))
            return;

        // frame.Payload points into a pooled buffer that is recycled the moment this returns, so the
        // bytes are turned into something owned BEFORE the first await. This is the one rule a
        // consumer cannot get away with breaking: an await here and a write afterwards would read
        // whatever the next frame put in the buffer.
        byte[] document = Render(frame, settings);

        string path = Path.Combine(outputFolder, FileNameFor(frame.ChannelId));

        try
        {
            await File.WriteAllBytesAsync(path, document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logged and swallowed on purpose. A payload is a snapshot: the next one is along in
            // 33 ms, and faulting the plugin because a virus scanner held the file open for a moment
            // would trade a missed frame for a dead consumer.
            Logger.LogWarning(ex, "Could not write {Path}.", path);
        }
    }

    /// <inheritdoc />
    public ValueTask OnChannelClosedAsync(string channelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        // The file is left in place deliberately - it is a record of the last state, and an OBS
        // source reading it should not blank when a game exits mid-run. The throttle is forgotten so
        // a producer that comes back writes immediately.
        lastWriteTimestamp.Remove(channelId);

        Logger.LogInformation("Channel {ChannelId} closed.", channelId);

        return ValueTask.CompletedTask;
    }

    /// <summary>Whether enough time has passed since this channel's last write.</summary>
    private bool ShouldWrite(string channelId, TimeSpan minimumInterval)
    {
        long now = Stopwatch.GetTimestamp();

        if (minimumInterval <= TimeSpan.Zero)
        {
            lastWriteTimestamp[channelId] = now;
            return true;
        }

        if (lastWriteTimestamp.TryGetValue(channelId, out long previous)
            && Stopwatch.GetElapsedTime(previous, now) < minimumInterval)
        {
            return false;
        }

        lastWriteTimestamp[channelId] = now;
        return true;
    }

    /// <summary>Turns one frame into the bytes to write.</summary>
    private static byte[] Render(PayloadFrame frame, JsonWriterConfiguration settings)
    {
        // A codec this plugin cannot represent as text is described rather than mangled. Json and
        // Utf8Raw are bytes it can pass through; Blittable is a struct's raw memory and means
        // nothing without the layout, so it is reported as base64 and left for something that knows.
        string? encoded = frame.Codec switch
        {
            PayloadCodec.Json or PayloadCodec.Utf8Raw => null,
            _ => Convert.ToBase64String(frame.Payload.Span),
        };

        JsonNode? payload = encoded is not null
            ? JsonValue.Create(encoded)
            : ParseOrText(frame.Payload.Span, frame.Codec);

        JsonNode document = settings.IncludeFrameMetadata
            ? new JsonObject
            {
                ["channelId"] = frame.ChannelId,
                ["sequence"] = frame.Sequence,
                ["codec"] = frame.Codec.ToString(),
                ["payload"] = payload,
            }
            : payload ?? JsonValue.Create(string.Empty);

        // Pass the producer's own bytes straight through when there is nothing to add: no metadata,
        // no re-indent, already JSON. That is both the cheapest path and the most faithful one.
        if (!settings.IncludeFrameMetadata && !settings.Indent && frame.Codec == PayloadCodec.Json)
            return frame.Payload.ToArray();

        using MemoryStream stream = new(frame.Payload.Length + 128);

        using (Utf8JsonWriter writer = new(stream, settings.Indent ? IndentedOptions : default))
            document.WriteTo(writer);

        return stream.ToArray();
    }

    /// <summary>JSON as a node, anything else as a string.</summary>
    private static JsonNode? ParseOrText(ReadOnlySpan<byte> payload, PayloadCodec codec)
    {
        if (codec != PayloadCodec.Json)
            return JsonValue.Create(Encoding.UTF8.GetString(payload));

        try
        {
            // Utf8JsonReader rather than JsonNode.Parse(string): no intermediate string, and it is
            // the only way to reject malformed JSON before it reaches the file.
            Utf8JsonReader reader = new(payload);
            return JsonNode.Parse(ref reader);
        }
        catch (JsonException)
        {
            // A producer that declared Json and sent something else. Recorded as text rather than
            // dropped, because that is the evidence somebody debugging it needs.
            return JsonValue.Create(Encoding.UTF8.GetString(payload));
        }
    }

    /// <summary>A channel id as a file name.</summary>
    /// <remarks>
    /// Channel ids look like <c>srt/re4r/gamememory</c> - path separators and all - so they cannot be
    /// used as file names unaltered. Every invalid character becomes an underscore, which can collide
    /// in principle; two channels differing only in punctuation are not a case worth more code here.
    /// </remarks>
    private static string FileNameFor(string channelId)
    {
        Span<char> buffer = stackalloc char[channelId.Length];
        ReadOnlySpan<char> invalid = Path.GetInvalidFileNameChars();

        for (int i = 0; i < channelId.Length; i++)
            buffer[i] = invalid.Contains(channelId[i]) ? '_' : channelId[i];

        return string.Concat(buffer, ".json");
    }
}
