using System.IO.Pipes;
using System.Runtime.Versioning;
using SRTHost.Ipc;
using SRTOverlay.DirectX12;
using SRTOverlay.Protocol;
using SRTPluginBase.Abstractions;

namespace SRTOverlay.Spike;

/// <summary>
/// The step-5/6 pipe transport: the consumer side of the runner-to-shim overlay pipe, standing in for
/// the overlay plugin's runner. It creates the pipe server, waits for the shim to connect and send its
/// <see cref="SurfaceRequest"/>, publishes a <see cref="SurfacePublish"/> for every frame the producer
/// completes, and (step 6) drains the shim's inbound <see cref="OverlayMessageKind.OverlayState"/> and
/// <see cref="OverlayMessageKind.InputBatch"/> frames on a background reader.
/// </summary>
/// <remarks>
/// <para>
/// The framing is SRTHost.Ipc's 16-byte <see cref="FrameHeader"/> reused byte for byte - the whole
/// point of "over SRTHost.Ipc" - with each overlay message carried as a packed struct on
/// <see cref="IpcChannel.Control"/> with <see cref="PayloadCodec.Blittable"/> and the overlay message
/// kind in the header's <c>MessageKind</c> field. This is the C# mirror of the C++ shim's
/// <c>OverlayPipeClient</c>; the two must agree on those bytes.
/// </para>
/// <para>
/// The wash producer draws no ImGui and has no cursor, so it does not <i>act</i> on the forwarded
/// input - but it must still read those frames, both to prove the shim's input forwarding reaches a
/// consumer (the step-6 gate) and because a consumer that never read would let the pipe's buffer fill
/// and stall the shim's writes. So a background reader drains them and logs a running summary: the state
/// toggles (visible/interactive/focus) are logged as they change, and input is summarised periodically.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OverlayPipeTransport : ISurfaceTransport, IDisposable
{
    private readonly string pipeName;
    private readonly Action<string> log;
    private readonly NamedPipeServerStream server;
    private readonly Lock writeLock = new();
    private readonly CancellationTokenSource readerCancellation = new();

    private Task? reader;

    // Running input tally, logged periodically so an interactive session does not drown the console.
    private long inputEventsSeen;
    private long inputBatchesSeen;
    private long inputDropped;
    private long nextInputLogAt = 1;

    public OverlayPipeTransport(string pipeName, Action<string> log)
    {
        this.pipeName = pipeName;
        this.log = log;
        server = IpcPipe.CreateServer(pipeName);
    }

    /// <inheritdoc />
    public bool TryGetRequest(CancellationToken cancellation, out SurfaceRequest request)
    {
        request = default;

        try
        {
            log($@"Producer: waiting for the shim to connect on \\.\pipe\{pipeName}...");
            server.WaitForConnectionAsync(cancellation).GetAwaiter().GetResult();
            log("Producer: shim connected; waiting for its surface request...");

            // Read frames until the surface request arrives (or the shim drops the pipe). Everything the
            // shim sends after it is handled by the background reader started below.
            while (!cancellation.IsCancellationRequested)
            {
                if (!TryReadFrame(cancellation, out FrameHeader header, out byte[] payload))
                {
                    log("Producer: the shim disconnected before sending a surface request.");
                    return false;
                }

                if ((OverlayMessageKind)(ushort)header.MessageKind == OverlayMessageKind.SurfaceRequest &&
                    SurfaceRequest.TryRead(payload) is { } parsed)
                {
                    request = parsed;
                    StartBackgroundReader(cancellation);
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C or the driver shutting down while we waited.
        }
        catch (IOException exception)
        {
            log($"Producer: pipe error while awaiting the surface request: {exception.Message}");
        }

        return false;
    }

    /// <inheritdoc />
    public void SetAlive(bool alive)
    {
        // The shim reads liveness from each publish's Alive flag, which Publish sets true. On shutdown,
        // send one last publish with Alive = 0 so the shim stops compositing at once rather than waiting
        // for the pipe to drop.
        if (!alive)
            TrySendPublish(0, 0, alive: false);
    }

    /// <inheritdoc />
    public void Publish(uint latestIndex, ulong fenceValue) => TrySendPublish(latestIndex, fenceValue, alive: true);

    /// <summary>
    /// Drain the shim's inbound state and input frames until the producer stops or the pipe closes.
    /// </summary>
    /// <remarks>
    /// Runs on its own task so the animate loop's publishes (writes) and these reads use the two
    /// independent directions of the duplex pipe concurrently. Reads never touch <see cref="writeLock"/>.
    /// </remarks>
    private void StartBackgroundReader(CancellationToken producerCancellation)
    {
        CancellationToken token = CancellationTokenSource
            .CreateLinkedTokenSource(producerCancellation, readerCancellation.Token).Token;

        reader = Task.Run(() =>
        {
            try
            {
                while (!token.IsCancellationRequested && server.IsConnected)
                {
                    if (!TryReadFrame(token, out FrameHeader header, out byte[] payload))
                        break;

                    DispatchInbound(header, payload);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The shim detached or the game closed; the animate loop ends on its own cancellation.
            }
        }, token);
    }

    private void DispatchInbound(FrameHeader header, byte[] payload)
    {
        switch ((OverlayMessageKind)(ushort)header.MessageKind)
        {
            case OverlayMessageKind.OverlayState when OverlayState.TryRead(payload) is { } state:
                log($"Shim state: visible={state.Visible}, interactive={state.Interactive}, " +
                    $"focus={state.WindowFocused}, cursor=({state.CursorX},{state.CursorY}).");
                break;

            case OverlayMessageKind.InputBatch when InputBatchHeader.TryRead(payload) is { } batch:
                LogInputBatch(batch, payload);
                break;

            case OverlayMessageKind.SurfaceRequest:
                // A resize renegotiation. The wash producer does not recreate its surface for the gate,
                // but the shim resending is worth seeing.
                log("Shim: resent its surface request (a resize renegotiation).");
                break;

            default:
                break;
        }
    }

    private void LogInputBatch(InputBatchHeader batch, byte[] payload)
    {
        inputBatchesSeen++;
        inputEventsSeen += batch.Count;
        inputDropped += batch.Dropped;

        // Peek the first event's type so the log shows input is genuinely flowing, not just framing.
        OverlayInputType firstType = OverlayInputType.None;
        if (batch.Count > 0 && InputEvent.TryRead(payload.AsSpan(InputBatchHeader.Size)) is { } first)
            firstType = first.Type;

        // Log on a doubling schedule (1, 2, 4, 8, ...) so a long interactive session stays readable.
        if (inputBatchesSeen >= nextInputLogAt)
        {
            nextInputLogAt *= 2;
            log($"Shim input: {inputEventsSeen} events over {inputBatchesSeen} batches " +
                $"(latest starts with {firstType}, dropped {inputDropped}).");
        }
    }

    private void TrySendPublish(uint latestIndex, ulong fenceValue, bool alive)
    {
        if (!server.IsConnected)
            return;

        SurfacePublish publish = new()
        {
            LatestIndex = latestIndex,
            Alive = alive ? 1u : 0u,
            FenceValue = fenceValue,
        };

        Span<byte> payload = stackalloc byte[SurfacePublish.Size];
        publish.WriteTo(payload);

        try
        {
            WriteFrame(OverlayMessageKind.SurfacePublish, payload);
        }
        catch (IOException)
        {
            // The shim dropped the pipe (game closing, or the overlay was detached). Nothing to do; the
            // animation loop will end when it is cancelled.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WriteFrame(OverlayMessageKind kind, ReadOnlySpan<byte> payload)
    {
        FrameHeader header = new()
        {
            Channel = IpcChannel.Control,
            Codec = PayloadCodec.Blittable,
            MessageKind = (ControlMessageKind)(ushort)kind,
            PayloadLength = payload.Length,
        };

        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        header.Write(headerBytes);

        // A write and the background read are the two directions of the duplex pipe and may overlap, but
        // two writes must not: SetAlive(false) can arrive from a different thread than the animate loop's
        // Publish, so the write itself is serialised.
        lock (writeLock)
        {
            server.Write(headerBytes);
            server.Write(payload);
            server.Flush();
        }
    }

    private bool TryReadFrame(CancellationToken cancellation, out FrameHeader header, out byte[] payload)
    {
        header = default;
        payload = [];

        byte[] headerBytes = new byte[FrameHeader.Size];
        if (!TryReadExactly(headerBytes, cancellation))
            return false;

        header = FrameHeader.Read(headerBytes);
        if (header.PayloadLength == 0)
            return true;

        payload = new byte[header.PayloadLength];
        return TryReadExactly(payload, cancellation);
    }

    private bool TryReadExactly(Memory<byte> buffer, CancellationToken cancellation)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            // .AsTask() before blocking: ReadAsync on an asynchronous pipe returns an
            // IValueTaskSource-backed ValueTask, and calling GetAwaiter().GetResult() on one before it
            // completes throws "operation not valid". AsTask() gives a Task that blocks correctly.
            int got = server.ReadAsync(buffer[read..], cancellation).AsTask().GetAwaiter().GetResult();
            if (got == 0)
                return false; // the shim closed its end
            read += got;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        readerCancellation.Cancel();
        try
        {
            reader?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
        {
        }

        readerCancellation.Dispose();
        server.Dispose();
    }
}
