using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SRTHost.Core;
using SRTHost.Core.Routing;
using SRTPluginBase.Abstractions;

namespace SRTHost.ViewModels;

/// <summary>
/// Watches the live payload on one channel, and records a run of frames to a file.
/// </summary>
/// <remarks>
/// This replaces <c>develop</c>'s <c>Debug.razor</c>, which showed three hardcoded fixtures. It is
/// nearly free because the router already holds the bytes: the whole page is a tap, a throttle and a
/// text box.
/// <para>
/// The tap is attached only while this page is on screen. Every frame it sees is copied out of the
/// pooled buffer - it has to be, since that buffer is recycled the instant the last subscriber has
/// written it - so leaving the inspector attached would mean an allocation per frame per producer
/// for a panel nobody is looking at.
/// </para>
/// <para>
/// Rendering is throttled to 4 Hz rather than following the producer. A 30 Hz JSON tree is
/// unreadable by a person and costs a re-parse and a re-layout per frame; four updates a second is
/// as fast as anyone can actually read a value changing. Recording is deliberately <em>not</em>
/// throttled: it exists to capture what really went over the wire.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class InspectorViewModel : ViewModelBase, IDisposable
{
    /// <summary>How often the displayed payload is refreshed.</summary>
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(250);

    private readonly HostContext host;
    private readonly ILogger logger;
    private readonly DispatcherTimer render;
    private readonly Lock gate = new();

    private ObservedFrame? latest;
    private List<ObservedFrame>? recording;
    private int recordTarget;

    /// <summary>Channels currently being published.</summary>
    public ObservableCollection<ChannelSnapshot> Channels { get; } = [];

    [ObservableProperty]
    public partial ChannelSnapshot? SelectedChannel { get; set; }

    /// <summary>The most recent payload, decoded as far as it can be.</summary>
    [ObservableProperty]
    public partial string Payload { get; set; } = "Select a channel to watch it.";

    /// <summary>Sequence number, timestamp and size of the frame being shown.</summary>
    [ObservableProperty]
    public partial string FrameInfo { get; set; } = string.Empty;

    /// <summary>How many frames to capture when recording.</summary>
    [ObservableProperty]
    public partial int RecordCount { get; set; } = 300;

    /// <summary>Whether a recording is in progress.</summary>
    [ObservableProperty]
    public partial bool Recording { get; set; }

    /// <summary>Where the last recording was written, for the page to show.</summary>
    [ObservableProperty]
    public partial string? RecordingStatus { get; set; }

    /// <summary>Creates the inspector.</summary>
    public InspectorViewModel(HostContext host)
    {
        ArgumentNullException.ThrowIfNull(host);

        this.host = host;
        logger = host.LoggerFactory.CreateLogger<InspectorViewModel>();

        render = new DispatcherTimer(RenderInterval, DispatcherPriority.Background, (_, _) => Render());
    }

    /// <inheritdoc />
    public override string Title => "Data";

    /// <inheritdoc />
    public override void Activated()
    {
        RefreshChannels();

        host.Runtime.Router.FrameObserver = OnFrame;
        render.Start();
    }

    /// <inheritdoc />
    public override void Deactivated()
    {
        render.Stop();

        // Detached even mid-recording. A recording is a deliberate act with a visible progress
        // count; leaving a tap on every routed frame because a page was navigated away from is not.
        host.Runtime.Router.FrameObserver = null;

        StopRecording();
    }

    /// <summary>Re-reads what the router is currently publishing.</summary>
    [RelayCommand]
    private void RefreshChannels()
    {
        string? selected = SelectedChannel?.ChannelId;

        Channels.Clear();

        foreach (ChannelSnapshot channel in host.Runtime.Router.Channels)
            Channels.Add(channel);

        // Cast through the nullable, because ChannelSnapshot is a struct: FirstOrDefault would
        // otherwise answer a zeroed snapshot rather than null when nothing matches.
        SelectedChannel = Channels.Cast<ChannelSnapshot?>().FirstOrDefault(channel =>
            string.Equals(channel!.Value.ChannelId, selected, StringComparison.OrdinalIgnoreCase))
            ?? Channels.Cast<ChannelSnapshot?>().FirstOrDefault();
    }

    /// <summary>Captures the next <see cref="RecordCount"/> frames on the selected channel to a file.</summary>
    [RelayCommand]
    private void Record()
    {
        if (SelectedChannel is null || Recording)
            return;

        lock (gate)
        {
            recordTarget = Math.Clamp(RecordCount, 1, 10_000);
            recording = new List<ObservedFrame>(recordTarget);
        }

        Recording = true;
        RecordingStatus = $"Recording 0 / {recordTarget}…";
    }

    /// <summary>Ends a recording early, keeping what has been captured.</summary>
    [RelayCommand]
    private void StopRecording()
    {
        List<ObservedFrame>? captured;

        lock (gate)
        {
            captured = recording;
            recording = null;
        }

        Recording = false;

        if (captured is { Count: > 0 })
            Write(captured);
    }

    /// <summary>
    /// Called on the producer runner's read loop, once per published frame.
    /// </summary>
    /// <remarks>
    /// Everything here is O(1) and non-blocking on purpose. This is the router's hot path: the
    /// producer is waiting on it, and anything slow becomes backpressure on the whole channel.
    /// Rendering and file writing happen elsewhere, on a timer.
    /// </remarks>
    private void OnFrame(ObservedFrame frame)
    {
        if (SelectedChannel is not { } channel
            || !string.Equals(frame.ChannelId, channel.ChannelId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        List<ObservedFrame>? finished = null;

        lock (gate)
        {
            latest = frame;

            if (recording is { } capture)
            {
                capture.Add(frame);

                if (capture.Count >= recordTarget)
                {
                    finished = capture;
                    recording = null;
                }
            }
        }

        if (finished is not null)
            Dispatcher.UIThread.Post(() => Finish(finished));
    }

    private void Finish(List<ObservedFrame> captured)
    {
        Recording = false;
        Write(captured);
    }

    private void Render()
    {
        ObservedFrame? frame;
        int captured;

        lock (gate)
        {
            frame = latest;
            captured = recording?.Count ?? 0;
        }

        if (Recording)
            RecordingStatus = $"Recording {captured} / {recordTarget}…";

        if (frame is null)
            return;

        FrameInfo = $"#{frame.Sequence}  {frame.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  "
            + $"{frame.Payload.Length:N0} bytes  {frame.Codec}";

        Payload = Format(frame);
    }

    /// <summary>
    /// Renders a payload as text, without ever pretending to understand it.
    /// </summary>
    /// <remarks>
    /// The router forwards opaque bytes and the host has no access to the payload's type - that is
    /// the property that lets one AnyCPU router sit between an x86 and an x64 plugin. So JSON is
    /// re-indented, and anything else is shown as a hex dump rather than guessed at. A binary payload
    /// shown as mojibake would be a worse answer than one shown as bytes.
    /// </remarks>
    private static string Format(ObservedFrame frame)
    {
        if (frame.Codec != PayloadCodec.Json)
            return HexDump(frame.Payload.Span);

        try
        {
            using JsonDocument document = JsonDocument.Parse(frame.Payload);

            return JsonSerializer.Serialize(document, InspectorJsonContext.Default.JsonDocument);
        }
        catch (JsonException)
        {
            // A producer that declared JSON and sent something else. Showing the bytes is how you
            // find that out.
            return HexDump(frame.Payload.Span);
        }
    }

    private static string HexDump(ReadOnlySpan<byte> payload)
    {
        const int BytesPerLine = 16;
        const int MaximumBytes = 4096;

        ReadOnlySpan<byte> shown = payload.Length > MaximumBytes ? payload[..MaximumBytes] : payload;

        StringBuilder text = new(shown.Length * 4);

        for (int offset = 0; offset < shown.Length; offset += BytesPerLine)
        {
            ReadOnlySpan<byte> line = shown[offset..Math.Min(offset + BytesPerLine, shown.Length)];

            text.Append($"{offset:x8}  ");

            for (int index = 0; index < BytesPerLine; index++)
                text.Append(index < line.Length ? $"{line[index]:x2} " : "   ");

            text.Append(' ');

            foreach (byte value in line)
                text.Append(value is >= 0x20 and < 0x7f ? (char)value : '.');

            text.AppendLine();
        }

        if (payload.Length > shown.Length)
            text.AppendLine($"… {payload.Length - shown.Length:N0} more bytes");

        return text.ToString();
    }

    /// <summary>Writes a recording to the host's state directory as newline-delimited JSON.</summary>
    /// <remarks>
    /// One object per line rather than one array, so a capture of ten thousand frames can be read
    /// with a streaming reader, grepped, or truncated without becoming invalid. The payload is
    /// embedded raw when it is JSON and base64 otherwise, which is the only lossless option for a
    /// codec the host cannot decode.
    /// </remarks>
    private void Write(List<ObservedFrame> captured)
    {
        try
        {
            string directory = Path.Combine(HostPaths.DataDirectory, "captures");

            Directory.CreateDirectory(directory);

            string path = Path.Combine(
                directory,
                $"{Sanitize(captured[0].ChannelId)}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

            using StreamWriter file = new(path, append: false, new UTF8Encoding(false));

            foreach (ObservedFrame frame in captured)
            {
                string payload = frame.Codec == PayloadCodec.Json
                    ? Encoding.UTF8.GetString(frame.Payload.Span)
                    : JsonSerializer.Serialize(Convert.ToBase64String(frame.Payload.Span), InspectorJsonContext.Default.String);

                file.WriteLine(
                    $"{{\"sequence\":{frame.Sequence},"
                    + $"\"timestamp\":\"{frame.TimestampUtc:O}\","
                    + $"\"channel\":{JsonSerializer.Serialize(frame.ChannelId, InspectorJsonContext.Default.String)},"
                    + $"\"codec\":\"{frame.Codec}\","
                    + $"\"payload\":{payload}}}");
            }

            RecordingStatus = $"Wrote {captured.Count:N0} frames to {path}";

            logger.LogInformation("Recorded {Count} frames to {Path}.", captured.Count, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordingStatus = $"Could not write the recording: {ex.Message}";
            logger.LogError(ex, "Could not write a channel recording.");
        }
    }

    /// <summary>Turns a channel id into something that can be a file name.</summary>
    private static string Sanitize(string channelId)
        => string.Concat(channelId.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    partial void OnSelectedChannelChanged(ChannelSnapshot? value)
    {
        lock (gate)
            latest = null;

        FrameInfo = string.Empty;
        Payload = value is null ? "No producer is publishing anything right now." : "Waiting for a frame…";
    }

    /// <inheritdoc />
    public void Dispose() => Deactivated();
}

/// <summary>Source-generated serialisation for the inspector's own output.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonDocument))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class InspectorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
