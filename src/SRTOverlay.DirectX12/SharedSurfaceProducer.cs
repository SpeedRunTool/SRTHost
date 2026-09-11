using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using SRTOverlay.Protocol;

namespace SRTOverlay.DirectX12;

/// <summary>
/// The throwaway "another process" for overlay spike step 3: it reads the shim's surface request,
/// creates the shared textures and fence on the game's adapter, and animates a wash into them so the
/// compositing can be judged by eye.
/// </summary>
/// <remarks>
/// <para>
/// This is not a plugin and not part of the shipped shim - it stands in for the overlay plugin's
/// runner, which in the real design (section 11, spike step 5) renders ImGui into the same shared
/// textures and drives the same fence. What it proves is exactly step 3's gate: that a texture a
/// <i>second process</i> wrote, on the adapter the shim named, appears in the game's frame. It draws
/// nothing recognisable as a HUD on purpose - a cycling colour wash over the whole surface is the most
/// unmistakable possible "this is live and it is ours", and it needs no shader, font atlas or layout.
/// </para>
/// <para>
/// It lives in <c>SRTOverlay.DirectX12</c> so it can share the raw interop and the shared-surface
/// layout with the compositor, but nothing in the shim's call graph reaches it, so the NativeAOT
/// publish trims it out of the injected binary entirely. It runs under ordinary CoreCLR in the spike
/// driver, where a GPU device and a 30 Hz render loop are unremarkable.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class SharedSurfaceProducer
{
    private const int TargetFormat = Direct3D12.DXGI_FORMAT_R8G8B8A8_UNORM;
    private const float WashAlpha = 0.5f;

    private readonly string session;
    private readonly Action<string> log;

    public SharedSurfaceProducer(string session, Action<string> log)
    {
        this.session = session;
        this.log = log;
    }

    /// <summary>
    /// Wait for the shim's request, create the surface, and animate it until cancelled.
    /// </summary>
    /// <returns><see langword="false"/> if the surface could never be created.</returns>
    public bool Run(CancellationToken cancellation)
    {
        if (!TryReadRequest(cancellation, out OverlaySharedSurface.RequestBlock request))
            return false;

        long luid = ((long)request.AdapterLuidHigh << 32) | request.AdapterLuidLow;
        log($"Producer: request is {request.Width}x{request.Height}, format {request.Format}, adapter LUID 0x{luid:X}.");

        nint factory = 0, adapter = 0, device = 0, queue = 0, allocator = 0, list = 0, fence = 0, sharedFence = 0, rtvHeap = 0;
        nint eventHandle = 0;
        nint[] textures = new nint[OverlaySharedSurface.SurfaceCount];

        // Created shared handles must stay open for the producer's whole life: the name a handle
        // registers stops resolving through OpenSharedHandleByName the moment the last created handle
        // to it closes, so closing them early would make the shim unable to open the surface.
        List<nint> sharedHandles = [];
        MemoryMappedFile? publishMap = null;
        MemoryMappedViewAccessor? publishView = null;
        byte* publishPtr = null;

        try
        {
            if (Direct3D12.CreateDXGIFactory1(Direct3D12.IID_IDXGIFactory4, out factory) < 0 || factory == 0)
                return Fail("CreateDXGIFactory1");

            if (Direct3D12.EnumAdapterByLuid(factory, luid, Direct3D12.IID_IDXGIAdapter1, out adapter) < 0 || adapter == 0)
                return Fail("EnumAdapterByLuid - the game's adapter could not be found; a shared handle will not open across adapters");

            if (Direct3D12.D3D12CreateDevice(adapter, Direct3D12.D3D_FEATURE_LEVEL_11_0, Direct3D12.IID_ID3D12Device, out device) < 0 || device == 0)
                return Fail("D3D12CreateDevice on the game's adapter");

            Direct3D12.D3D12CommandQueueDesc queueDesc = new()
            {
                Type = Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT,
                Priority = 0,
                Flags = Direct3D12.D3D12_COMMAND_QUEUE_FLAG_NONE,
                NodeMask = 0,
            };
            if (Direct3D12.CreateCommandQueue(device, queueDesc, Direct3D12.IID_ID3D12CommandQueue, out queue) < 0 || queue == 0)
                return Fail("CreateCommandQueue");

            if (Direct3D12.CreateCommandAllocator(device, Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT, Direct3D12.IID_ID3D12CommandAllocator, out allocator) < 0 || allocator == 0)
                return Fail("CreateCommandAllocator");

            if (Direct3D12.CreateCommandList(device, 0, Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, 0, Direct3D12.IID_ID3D12GraphicsCommandList, out list) < 0 || list == 0)
                return Fail("CreateCommandList");
            Direct3D12.CommandListClose(list);

            if (Direct3D12.CreateFence(device, 0, Direct3D12.D3D12_FENCE_FLAG_NONE, Direct3D12.IID_ID3D12Fence, out fence) < 0 || fence == 0)
                return Fail("CreateFence (local)");

            // The fence the shim waits on: shared, and published by name.
            if (Direct3D12.CreateFence(device, 0, Direct3D12.D3D12_FENCE_FLAG_SHARED, Direct3D12.IID_ID3D12Fence, out sharedFence) < 0 || sharedFence == 0)
                return Fail("CreateFence (shared)");
            if (Direct3D12.CreateSharedHandle(device, sharedFence, OverlaySharedSurface.FenceName(session), out nint fenceHandle) < 0 || fenceHandle == 0)
                return Fail("CreateSharedHandle (fence)");
            sharedHandles.Add(fenceHandle); // held open so the name stays resolvable for the shim

            // The RTV heap for clearing our own textures.
            Direct3D12.DescriptorHeapDesc rtvHeapDesc = new()
            {
                Type = Direct3D12.D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
                NumDescriptors = OverlaySharedSurface.SurfaceCount,
                Flags = Direct3D12.D3D12_DESCRIPTOR_HEAP_FLAG_NONE,
                NodeMask = 0,
            };
            if (Direct3D12.CreateDescriptorHeap(device, rtvHeapDesc, Direct3D12.IID_ID3D12DescriptorHeap, out rtvHeap) < 0 || rtvHeap == 0)
                return Fail("CreateDescriptorHeap (RTV)");

            uint rtvIncrement = Direct3D12.GetDescriptorHandleIncrementSize(device, Direct3D12.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
            nuint rtvBase = Direct3D12.GetCpuDescriptorHandleForHeapStart(rtvHeap);

            for (int i = 0; i < textures.Length; i++)
            {
                if (!CreateSharedTexture(device, request.Width, request.Height, out textures[i]))
                    return Fail($"creating shared texture {i}");

                if (Direct3D12.CreateSharedHandle(device, textures[i], OverlaySharedSurface.TextureName(session, i), out nint texHandle) < 0 || texHandle == 0)
                    return Fail($"CreateSharedHandle (texture {i})");
                sharedHandles.Add(texHandle); // held open for the producer's life, as above

                Direct3D12.CreateRenderTargetView(device, textures[i], rtvBase + (nuint)(i * rtvIncrement));
            }

            eventHandle = Win32.CreateEventW(0, 0, 0, null);
            if (eventHandle == 0)
                return Fail("CreateEventW");

            // Publish block: producer-owned. CreateOrOpen so a stale mapping from a previous run is reused.
            publishMap = MemoryMappedFile.CreateOrOpen(OverlaySharedSurface.PublishName(session), sizeof(OverlaySharedSurface.PublishBlock));
            publishView = publishMap.CreateViewAccessor();
            publishView.SafeMemoryMappedViewHandle.AcquirePointer(ref publishPtr);
            OverlaySharedSurface.PublishBlock* publish = (OverlaySharedSurface.PublishBlock*)publishPtr;
            *publish = default;
            publish->Version = OverlaySharedSurface.SurfaceVersion;
            publish->Alive = 1;
            publish->Magic = OverlaySharedSurface.Magic;

            log("Producer: surface created and published; animating. Press Ctrl+C or cancel to stop.");
            AnimateUntilCancelled(device, queue, allocator, list, fence, sharedFence, eventHandle, rtvHeap, rtvIncrement, textures, publish, cancellation);
            return true;
        }
        finally
        {
            if (publishPtr is not null)
            {
                ((OverlaySharedSurface.PublishBlock*)publishPtr)->Alive = 0;
                publishView!.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            publishView?.Dispose();
            publishMap?.Dispose();

            if (eventHandle != 0)
                Win32.CloseHandle(eventHandle);

            foreach (nint handle in sharedHandles)
                Win32.CloseHandle(handle);

            foreach (nint texture in textures)
                Direct3D12.Release(texture);
            Direct3D12.Release(rtvHeap);
            Direct3D12.Release(sharedFence);
            Direct3D12.Release(fence);
            Direct3D12.Release(list);
            Direct3D12.Release(allocator);
            Direct3D12.Release(queue);
            Direct3D12.Release(device);
            Direct3D12.Release(adapter);
            Direct3D12.Release(factory);
        }
    }

    private bool TryReadRequest(CancellationToken cancellation, out OverlaySharedSurface.RequestBlock request)
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
                        request = *block;
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

    private void AnimateUntilCancelled(
        nint device, nint queue, nint allocator, nint list, nint fence, nint sharedFence, nint eventHandle,
        nint rtvHeap, uint rtvIncrement, nint[] textures, OverlaySharedSurface.PublishBlock* publish,
        CancellationToken cancellation)
    {
        nuint rtvBase = Direct3D12.GetCpuDescriptorHandleForHeapStart(rtvHeap);
        ulong fenceValue = 0;
        ulong sharedValue = 0;
        int index = 0;
        long startTicks = Environment.TickCount64;

        // One clear-colour buffer for the life of the loop. Allocating inside the loop would either
        // grow the stack (stackalloc is freed at method return, not per iteration) or allocate managed
        // memory every frame; neither is wanted in a render loop.
        float* colour = stackalloc float[4];

        while (!cancellation.IsCancellationRequested)
        {
            // Alternate textures so the one the shim is reading is never the one being written.
            index ^= 1;

            float seconds = (Environment.TickCount64 - startTicks) / 1000f;
            FillHsvPremultiplied((seconds * 60f) % 360f, WashAlpha, colour);

            Direct3D12.CommandAllocatorReset(allocator);
            Direct3D12.CommandListReset(list, allocator, 0);

            Direct3D12.ResourceBarrierDesc barrier = new()
            {
                Type = Direct3D12.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
                Resource = textures[index],
                Subresource = unchecked((uint)Direct3D12.D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES),
                StateBefore = Direct3D12.D3D12_RESOURCE_STATE_COMMON,
                StateAfter = Direct3D12.D3D12_RESOURCE_STATE_RENDER_TARGET,
            };
            Direct3D12.ResourceBarrier(list, &barrier, 1);

            nuint rtvHandle = rtvBase + (nuint)(index * rtvIncrement);
            Direct3D12.ClearRenderTargetView(list, rtvHandle, colour);

            // Back to COMMON so the shim's implicit promotion to a shader-resource read is legal.
            barrier.StateBefore = Direct3D12.D3D12_RESOURCE_STATE_RENDER_TARGET;
            barrier.StateAfter = Direct3D12.D3D12_RESOURCE_STATE_COMMON;
            Direct3D12.ResourceBarrier(list, &barrier, 1);

            Direct3D12.CommandListClose(list);

            nint executed = list;
            Direct3D12.ExecuteCommandLists(queue, 1, &executed);

            // Signal the shared fence: the shim waits this value on its queue before sampling index.
            sharedValue++;
            Direct3D12.QueueSignal(queue, sharedFence, sharedValue);

            // Publish which index is newest and when it will be done. Write the payload before the
            // index, with a barrier, so the shim never reads a new index against a stale fence value.
            publish->FenceValue = sharedValue;
            Thread.MemoryBarrier();
            publish->LatestIndex = (uint)index;

            // Throttle to the producer's own tick rate, and reclaim the allocator for the next clear.
            fenceValue++;
            Direct3D12.QueueSignal(queue, fence, fenceValue);
            if (Direct3D12.GetFenceCompletedValue(fence) < fenceValue)
            {
                Direct3D12.FenceSetEventOnCompletion(fence, fenceValue, eventHandle);
                Win32.WaitForSingleObject(eventHandle, 1000);
            }

            Thread.Sleep(33); // ~30 Hz, the rate a real producer plugin ticks at
        }

        // Drain before the finally block releases the textures the GPU may still be clearing.
        fenceValue++;
        Direct3D12.QueueSignal(queue, fence, fenceValue);
        if (Direct3D12.GetFenceCompletedValue(fence) < fenceValue)
        {
            Direct3D12.FenceSetEventOnCompletion(fence, fenceValue, eventHandle);
            Win32.WaitForSingleObject(eventHandle, 1000);
        }
    }

    private static bool CreateSharedTexture(nint device, uint width, uint height, out nint texture)
    {
        Direct3D12.HeapProperties heap = new()
        {
            Type = Direct3D12.D3D12_HEAP_TYPE_DEFAULT,
            CpuPageProperty = 0,
            MemoryPoolPreference = 0,
            CreationNodeMask = 1,
            VisibleNodeMask = 1,
        };

        Direct3D12.ResourceDesc desc = new()
        {
            Dimension = Direct3D12.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = TargetFormat,
            SampleDesc = new Direct3D12.DxgiSampleDesc { Count = 1, Quality = 0 },
            Layout = Direct3D12.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            Flags = Direct3D12.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
        };

        // Shared resources start in COMMON; the animation loop transitions each to RENDER_TARGET to
        // clear it and back to COMMON to hand it over.
        int hr = Direct3D12.CreateCommittedResource(
            device, heap, Direct3D12.D3D12_HEAP_FLAG_SHARED, desc,
            Direct3D12.D3D12_RESOURCE_STATE_COMMON, Direct3D12.IID_ID3D12Resource, out texture);
        return hr >= 0 && texture != 0;
    }

    /// <summary>Write a fully-saturated hue as a premultiplied RGBA clear colour into <paramref name="rgba"/>.</summary>
    /// <remarks>
    /// Premultiplied because the composite blend is <c>Src*ONE + Dst*(1-SrcA)</c>: the texture must
    /// already carry colour multiplied by its alpha. Value and saturation are both one, so the chroma
    /// is just the alpha and the three colour channels are scaled straight by it.
    /// </remarks>
    private static void FillHsvPremultiplied(float hueDegrees, float alpha, float* rgba)
    {
        float h = hueDegrees / 60f;
        float c = alpha;
        float x = c * (1f - MathF.Abs(h % 2f - 1f));

        (float r, float g, float b) = (int)h switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };

        rgba[0] = r;
        rgba[1] = g;
        rgba[2] = b;
        rgba[3] = alpha;
    }

    private bool Fail(string what)
    {
        log($"Producer: {what} failed.");
        return false;
    }
}
