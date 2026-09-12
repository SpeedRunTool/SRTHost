using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using SRTOverlay.Protocol;

namespace SRTOverlay.DirectX12;

/// <summary>
/// The step-3 shared-memory transport: the shim writes a request block and the producer writes a
/// publish block, both in named file mappings. Retained for the superseded C# shim, which still reads
/// and writes these blocks; the C++ shim negotiates over the pipe instead (see
/// <c>OverlayPipeTransport</c> in the spike).
/// </summary>
/// <remarks>
/// This is the negotiation code moved verbatim out of <see cref="SharedSurfaceProducer"/> when its
/// transport was made pluggable - the block layout, the poll for the request, the barrier between the
/// fence value and the index, and the <c>Alive</c> handshake are all unchanged.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class SharedMemoryTransport : ISurfaceTransport, IDisposable
{
    private readonly string session;
    private readonly Action<string> log;

    private MemoryMappedFile? publishMap;
    private MemoryMappedViewAccessor? publishView;
    private byte* publishPtr;

    public SharedMemoryTransport(string session, Action<string> log)
    {
        this.session = session;
        this.log = log;
    }

    /// <inheritdoc />
    public bool TryGetRequest(CancellationToken cancellation, out SurfaceRequest request)
    {
        request = default;
        log("Producer: waiting for the shim to publish a surface request...");

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using MemoryMappedFile map = MemoryMappedFile.OpenExisting(OverlaySharedSurface.RequestName(session));
                using MemoryMappedViewAccessor view = map.CreateViewAccessor();
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    OverlaySharedSurface.RequestBlock* block = (OverlaySharedSurface.RequestBlock*)pointer;
                    if (block->Magic == OverlaySharedSurface.Magic &&
                        block->State == (uint)OverlaySharedSurface.RequestState.Requested)
                    {
                        request = new SurfaceRequest
                        {
                            Width = block->Width,
                            Height = block->Height,
                            Format = block->Format,
                            AdapterLuidLow = block->AdapterLuidLow,
                            AdapterLuidHigh = block->AdapterLuidHigh,
                            SurfaceCount = (uint)OverlaySharedSurface.SurfaceCount,
                        };
                        return true;
                    }
                }
                finally
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
            catch (FileNotFoundException)
            {
                // The shim has not created the request block yet.
            }

            Thread.Sleep(100);
        }

        log("Producer: cancelled before a request appeared.");
        return false;
    }

    /// <inheritdoc />
    public void SetAlive(bool alive)
    {
        if (alive)
        {
            // Publish block: producer-owned. CreateOrOpen so a stale mapping from a previous run is reused.
            publishMap = MemoryMappedFile.CreateOrOpen(OverlaySharedSurface.PublishName(session), sizeof(OverlaySharedSurface.PublishBlock));
            publishView = publishMap.CreateViewAccessor();
            byte* pointer = null;
            publishView.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            publishPtr = pointer;

            OverlaySharedSurface.PublishBlock* publish = (OverlaySharedSurface.PublishBlock*)publishPtr;
            *publish = default;
            publish->Version = OverlaySharedSurface.SurfaceVersion;
            publish->Alive = 1;
            publish->Magic = OverlaySharedSurface.Magic;
            return;
        }

        if (publishPtr is not null)
            ((OverlaySharedSurface.PublishBlock*)publishPtr)->Alive = 0;
    }

    /// <inheritdoc />
    public void Publish(uint latestIndex, ulong fenceValue)
    {
        if (publishPtr is null)
            return;

        OverlaySharedSurface.PublishBlock* publish = (OverlaySharedSurface.PublishBlock*)publishPtr;

        // Write the payload before the index, with a barrier, so the shim never reads a new index
        // against a stale fence value.
        publish->FenceValue = fenceValue;
        Thread.MemoryBarrier();
        publish->LatestIndex = latestIndex;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (publishPtr is not null)
        {
            publishView!.SafeMemoryMappedViewHandle.ReleasePointer();
            publishPtr = null;
        }
        publishView?.Dispose();
        publishMap?.Dispose();
        publishView = null;
        publishMap = null;
    }
}
