using System.IO.Pipes;
using System.Runtime.Versioning;
using SRTHost.Ipc;
using SRTOverlay.DirectX12;
using SRTOverlay.Protocol;
using SRTPluginBase.Abstractions;

namespace SRTOverlay.Spike;

/// <summary>
/// The step-5 pipe transport: the consumer side of the runner-to-shim overlay pipe, standing in for
/// the overlay plugin's runner. It creates the pipe server, waits for the shim to connect and send its
/// <see cref="SurfaceRequest"/>, and publishes a <see cref="SurfacePublish"/> for every frame the
/// producer completes.
/// </summary>
/// <remarks>
/// The framing is SRTHost.Ipc's 16-byte <see cref="FrameHeader"/> reused byte for byte - the whole
/// point of "over SRTHost.Ipc" - with each overlay message carried as a packed struct on
/// <see cref="IpcChannel.Control"/> with <see cref="PayloadCodec.Blittable"/> and the overlay message
/// kind in the header's <c>MessageKind</c> field. This is the C# mirror of the C++ shim's
/// <c>OverlayPipeClient</c>; the two must agree on those bytes.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OverlayPipeTransport : ISurfaceTransport, IDisposable
{
    private readonly string pipeName;
    private readonly Action<string> log;
    private readonly NamedPipeServerStream server;

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

            // The only shim-to-consumer message before step 6 is the surface request; read frames until
            // one arrives (or the shim drops the pipe).
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

        server.Write(headerBytes);
        server.Write(payload);
        server.Flush();
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
    public void Dispose() => server.Dispose();
}
