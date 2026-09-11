using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using SRTOverlay.Core;
using SRTOverlay.Protocol;

namespace SRTOverlay.DirectX12;

/// <summary>
/// Draws one textured quad into the game's back buffer each frame - a texture another process
/// rendered into shared memory on the game's own adapter. This is the whole of what the shim draws.
/// </summary>
/// <remarks>
/// <para>
/// Spike step 3 of section 11, and the point at which the shared-texture design is proven or is not.
/// The shim owns no picture of its own: it opens a texture and a fence the producer created with
/// <c>D3D12_HEAP_FLAG_SHARED</c> / <c>D3D12_FENCE_FLAG_SHARED</c>, waits the fence on the game's queue
/// so the GPU orders the two processes' work without the render thread ever blocking, and composites
/// one quad with premultiplied alpha. Everything expressive happens in the producer's process; a bug
/// there crashes the producer, not the game.
/// </para>
/// <para>
/// <b>Nothing here allocates on the steady-state <c>Present</c> path.</b> The heaps, the pipeline, the
/// per-buffer allocators, the descriptors and the shared handles are all built once (and rebuilt on a
/// resize). A composited frame is a handful of vtable calls through pre-resolved function pointers and
/// no managed allocation, which is what the under-a-millisecond budget requires. It is not reentrant:
/// it only ever runs on the game's render thread, inside the hooked <c>Present</c>.
/// </para>
/// <para>
/// The two control blocks are shared memory standing in for the two runner-to-shim pipe messages that
/// step 5 will use; see <see cref="OverlaySharedSurface"/>. The shim writes the request once, then
/// polls for the producer's publish block and opens the surfaces the first time it appears.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class D3D12Compositor : IDisposable
{
    private readonly string session;

    private nint device;
    private nint queue;
    private nint rootSignature;
    private nint pipelineState;
    private nint rtvHeap;
    private nint srvHeap;
    private uint rtvIncrement;
    private uint srvIncrement;
    private ulong srvGpuBase;

    private uint bufferCount;
    private int format;
    private uint width;
    private uint height;

    // Colour-space adaptation for the pixel shader: a brightness multiplier and whether to PQ-encode.
    // Chosen per back-buffer format so the same SDR overlay reads correctly on SDR, scRGB-float and
    // HDR10 targets, and overridable from the startup options to tune the HDR look without rebuilding.
    private float brightness = 1f;
    private bool pqEncode;

    private nint[] renderTargets = [];
    private nint[] commandAllocators = [];
    private ulong[] frameFenceValues = [];
    private nint commandList;
    private nint frameFence;
    private nint fenceEvent;
    private ulong fenceValue;

    private bool ready;

    // The producer's side, opened lazily once it shows up.
    private MemoryMappedFile? requestMap;
    private MemoryMappedViewAccessor? requestView;
    private byte* requestPtr;
    private MemoryMappedFile? publishMap;
    private MemoryMappedViewAccessor? publishView;
    private byte* publishPtr;
    private readonly nint[] sharedTextures = new nint[OverlaySharedSurface.SurfaceCount];
    private nint sharedFence;
    private bool surfacesOpened;

    private long composited;
    private long skipped;

    internal D3D12Compositor(string session) => this.session = session;

    internal bool IsReady => Volatile.Read(ref ready);

    /// <summary>How many frames have actually been composited, for the self-check's round-trip.</summary>
    internal long CompositedFrames => Interlocked.Read(ref composited);

    /// <summary>Whether the surfaces are open, and how many frames have been composited or skipped.</summary>
    internal string DescribeActivity()
        => $"composited {Interlocked.Read(ref composited):N0}, skipped {Interlocked.Read(ref skipped):N0}, " +
           $"surfaces {(surfacesOpened ? "open" : "waiting for producer")}";

    /// <summary>
    /// Build every device resource the composite needs, and publish the surface request. Called once,
    /// on the render thread, after <c>Present</c> has handed us the swapchain and a DIRECT queue.
    /// </summary>
    /// <returns><see langword="false"/> if anything failed; the caller then composites nothing.</returns>
    internal bool Initialize(nint swapChain, nint commandQueue)
    {
        queue = commandQueue;

        int hr = Direct3D12.GetDeviceOf(swapChain, Direct3D12.IID_ID3D12Device, out device);
        if (hr < 0 || device == 0)
        {
            OverlayLog.Write($"Compositor: GetDevice failed, 0x{hr:X8}.");
            return false;
        }

        hr = Direct3D12.GetSwapChainDesc(swapChain, out Direct3D12.DxgiSwapChainDesc desc);
        if (hr < 0)
        {
            OverlayLog.Write($"Compositor: GetDesc failed, 0x{hr:X8}.");
            return false;
        }

        bufferCount = desc.BufferCount;
        width = desc.BufferDesc.Width;
        height = desc.BufferDesc.Height;
        format = desc.BufferDesc.Format;

        OverlayLog.Write($"Compositor: back buffer {width}x{height}, format {format}, {bufferCount} buffers.");

        // Adapt the composite to the back buffer's colour space rather than declining on anything but
        // SDR. A user's HDR or Frame-Generation settings are not ours to dictate, so every common
        // render-target format is handled: the pipeline draws in the buffer's own format and the pixel
        // shader scales (and, for HDR10, PQ-encodes) the SDR overlay to suit.
        if (!ChooseColorAdjust(format, out brightness, out pqEncode))
        {
            OverlayLog.Write(
                $"Compositor: back buffer format {format} is not a render-target format the composite " +
                "handles; not compositing. The hooks stay installed and pass through, so the game is " +
                "unaffected.");
            return false;
        }

        OverlayLog.Write($"Compositor: colour adjust for format {format}: brightness {brightness}, PQ-encode {pqEncode}.");

        OverlayLog.Write("Compositor: creating pipeline...");
        if (!CreatePipeline())
        {
            Shutdown();
            return false;
        }

        OverlayLog.Write("Compositor: creating descriptor heaps...");
        if (!CreateHeaps())
        {
            Shutdown();
            return false;
        }

        OverlayLog.Write("Compositor: acquiring back buffers and creating render targets...");
        if (!CreateRenderTargets(swapChain))
        {
            Shutdown();
            return false;
        }

        OverlayLog.Write("Compositor: creating per-frame command resources...");
        if (!CreateFrameResources())
        {
            Shutdown();
            return false;
        }

        OverlayLog.Write("Compositor: publishing the surface request...");
        if (!PublishRequest())
        {
            Shutdown();
            return false;
        }

        Volatile.Write(ref ready, true);
        OverlayLog.Write("Compositor: initialised; waiting for a producer to create the surface.");
        return true;
    }

    /// <summary>
    /// Pick the pixel-shader colour adjustment for a back-buffer format, honouring a startup-options
    /// override. Returns <see langword="false"/> for a format the composite cannot target at all.
    /// </summary>
    /// <remarks>
    /// The defaults are a sound first approximation, tunable live through the startup options:
    /// <list type="bullet">
    /// <item>8-bit UNORM (and sRGB) and 10-bit SDR: brightness 1, no encoding.</item>
    /// <item>scRGB float (<c>R16G16B16A16_FLOAT</c>): linear, scaled to a ~200-nit reference white
    /// (200/80), no encoding - float buffers blend correctly in linear space.</item>
    /// <item>An HDR10 10-bit buffer is indistinguishable from a 10-bit SDR one by format alone; it
    /// defaults to SDR passthrough (visible either way) and can be switched to PQ with the override.</item>
    /// </list>
    /// </remarks>
    private static bool ChooseColorAdjust(int format, out float brightness, out bool pqEncode)
    {
        brightness = 1f;
        pqEncode = false;

        switch (format)
        {
            case Direct3D12.DXGI_FORMAT_R8G8B8A8_UNORM:
            case Direct3D12.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
            case Direct3D12.DXGI_FORMAT_B8G8R8A8_UNORM:
            case Direct3D12.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
            case Direct3D12.DXGI_FORMAT_R10G10B10A2_UNORM:
                break;
            case Direct3D12.DXGI_FORMAT_R16G16B16A16_FLOAT:
                brightness = 200f / 80f; // scRGB reference white
                break;
            default:
                return false;
        }

        // Startup-options overrides let the HDR look be tuned live (e.g. flip a 10-bit buffer to PQ, or
        // change the reference-white brightness) without rebuilding the shim.
        OverlayStartupOptions? options = OverlayRuntime.Options;
        if (options?.OverlayBrightness is float b and > 0f)
            brightness = b;
        if (options?.OverlayForcePq is true)
            pqEncode = true;

        return true;
    }

    private bool CreatePipeline()
    {
        // Root signature: an SRV table for the pixel shader (t0), four root constants for the vertex
        // shader's placement rectangle (b0), and one static point-clamp sampler (s0).
        Direct3D12.DescriptorRange srvRange = new()
        {
            RangeType = Direct3D12.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            NumDescriptors = 1,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };

        Direct3D12.RootParameter* parameters = stackalloc Direct3D12.RootParameter[3];
        parameters[0] = default;
        parameters[0].ParameterType = Direct3D12.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[0].NumDescriptorRanges = 1;
        parameters[0].DescriptorRanges = (nint)(&srvRange);
        parameters[0].ShaderVisibility = Direct3D12.D3D12_SHADER_VISIBILITY_PIXEL;

        parameters[1] = default;
        parameters[1].ParameterType = Direct3D12.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[1].Constants_ShaderRegister = 0; // b0: placement rectangle (vertex shader)
        parameters[1].Constants_RegisterSpace = 0;
        parameters[1].Constants_Num32BitValues = 4;
        parameters[1].ShaderVisibility = Direct3D12.D3D12_SHADER_VISIBILITY_VERTEX;

        parameters[2] = default;
        parameters[2].ParameterType = Direct3D12.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[2].Constants_ShaderRegister = 1; // b1: colour adjust (pixel shader)
        parameters[2].Constants_RegisterSpace = 0;
        parameters[2].Constants_Num32BitValues = 4;
        parameters[2].ShaderVisibility = Direct3D12.D3D12_SHADER_VISIBILITY_PIXEL;

        Direct3D12.StaticSamplerDesc sampler = new()
        {
            Filter = Direct3D12.D3D12_FILTER_MIN_MAG_MIP_POINT,
            AddressU = Direct3D12.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressV = Direct3D12.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW = Direct3D12.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            ComparisonFunc = Direct3D12.D3D12_COMPARISON_FUNC_ALWAYS,
            BorderColor = Direct3D12.D3D12_STATIC_BORDER_COLOR_TRANSPARENT_BLACK,
            MaxLOD = 3.402823466e+38f, // D3D12_FLOAT32_MAX
            ShaderRegister = 0,
            RegisterSpace = 0,
            ShaderVisibility = Direct3D12.D3D12_SHADER_VISIBILITY_PIXEL,
        };

        Direct3D12.RootSignatureDesc rootDesc = new()
        {
            NumParameters = 3,
            Parameters = (nint)parameters,
            NumStaticSamplers = 1,
            StaticSamplers = (nint)(&sampler),
            Flags = Direct3D12.D3D12_ROOT_SIGNATURE_FLAG_NONE,
        };

        int hr = Direct3D12.D3D12SerializeRootSignature(&rootDesc, Direct3D12.D3D_ROOT_SIGNATURE_VERSION_1_0, out nint blob, out nint errorBlob);
        if (hr < 0 || blob == 0)
        {
            OverlayLog.Write($"Compositor: D3D12SerializeRootSignature failed, 0x{hr:X8}.");
            Direct3D12.Release(errorBlob);
            return false;
        }

        try
        {
            hr = Direct3D12.CreateRootSignature(device, Direct3D12.BlobGetBufferPointer(blob), Direct3D12.BlobGetBufferSize(blob), Direct3D12.IID_ID3D12RootSignature, out rootSignature);
        }
        finally
        {
            Direct3D12.Release(blob);
            Direct3D12.Release(errorBlob);
        }

        if (hr < 0 || rootSignature == 0)
        {
            OverlayLog.Write($"Compositor: CreateRootSignature failed, 0x{hr:X8}.");
            return false;
        }

        fixed (byte* vs = CompositeShaders.VertexShader)
        fixed (byte* ps = CompositeShaders.PixelShader)
        {
            Direct3D12.GraphicsPipelineStateDesc pso = default;
            pso.RootSignature = rootSignature;
            pso.VS = new Direct3D12.ShaderBytecode { BytecodePointer = (nint)vs, BytecodeLength = (nuint)CompositeShaders.VertexShader.Length };
            pso.PS = new Direct3D12.ShaderBytecode { BytecodePointer = (nint)ps, BytecodeLength = (nuint)CompositeShaders.PixelShader.Length };
            pso.SampleMask = Direct3D12.D3D12_DEFAULT_SAMPLE_MASK;

            // Premultiplied-alpha over the game's frame: the source is already multiplied by its
            // alpha, so the blend is Src*ONE + Dst*(1-SrcA).
            pso.BlendState.RenderTarget0 = new Direct3D12.RenderTargetBlendDesc
            {
                BlendEnable = 1,
                LogicOpEnable = 0,
                SrcBlend = Direct3D12.D3D12_BLEND_ONE,
                DestBlend = Direct3D12.D3D12_BLEND_INV_SRC_ALPHA,
                BlendOp = Direct3D12.D3D12_BLEND_OP_ADD,
                SrcBlendAlpha = Direct3D12.D3D12_BLEND_ONE,
                DestBlendAlpha = Direct3D12.D3D12_BLEND_INV_SRC_ALPHA,
                BlendOpAlpha = Direct3D12.D3D12_BLEND_OP_ADD,
                LogicOp = Direct3D12.D3D12_LOGIC_OP_NOOP,
                RenderTargetWriteMask = Direct3D12.D3D12_COLOR_WRITE_ENABLE_ALL,
            };

            pso.RasterizerState = new Direct3D12.RasterizerDesc
            {
                FillMode = Direct3D12.D3D12_FILL_MODE_SOLID,
                CullMode = Direct3D12.D3D12_CULL_MODE_NONE,
                DepthClipEnable = 1,
                ConservativeRaster = Direct3D12.D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF,
            };

            // Depth and stencil off: the overlay draws over the finished frame and reads no depth.
            pso.DepthStencilState = default;

            pso.PrimitiveTopologyType = Direct3D12.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
            pso.NumRenderTargets = 1;
            pso.RTVFormats.Format0 = format;
            pso.SampleDesc = new Direct3D12.DxgiSampleDesc { Count = 1, Quality = 0 };
            pso.Flags = Direct3D12.D3D12_PIPELINE_STATE_FLAG_NONE;

            int psoHr = Direct3D12.CreateGraphicsPipelineState(device, &pso, Direct3D12.IID_ID3D12PipelineState, out pipelineState);
            if (psoHr < 0 || pipelineState == 0)
            {
                OverlayLog.Write($"Compositor: CreateGraphicsPipelineState failed, 0x{psoHr:X8}.");
                return false;
            }
        }

        return true;
    }

    private bool CreateHeaps()
    {
        Direct3D12.DescriptorHeapDesc rtvDesc = new()
        {
            Type = Direct3D12.D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
            NumDescriptors = bufferCount,
            Flags = Direct3D12.D3D12_DESCRIPTOR_HEAP_FLAG_NONE,
            NodeMask = 0,
        };

        int hr = Direct3D12.CreateDescriptorHeap(device, rtvDesc, Direct3D12.IID_ID3D12DescriptorHeap, out rtvHeap);
        if (hr < 0 || rtvHeap == 0)
        {
            OverlayLog.Write($"Compositor: CreateDescriptorHeap (RTV) failed, 0x{hr:X8}.");
            return false;
        }

        Direct3D12.DescriptorHeapDesc srvDesc = new()
        {
            Type = Direct3D12.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            NumDescriptors = OverlaySharedSurface.SurfaceCount,
            Flags = Direct3D12.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
            NodeMask = 0,
        };

        hr = Direct3D12.CreateDescriptorHeap(device, srvDesc, Direct3D12.IID_ID3D12DescriptorHeap, out srvHeap);
        if (hr < 0 || srvHeap == 0)
        {
            OverlayLog.Write($"Compositor: CreateDescriptorHeap (SRV) failed, 0x{hr:X8}.");
            return false;
        }

        rtvIncrement = Direct3D12.GetDescriptorHandleIncrementSize(device, Direct3D12.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        srvIncrement = Direct3D12.GetDescriptorHandleIncrementSize(device, Direct3D12.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        srvGpuBase = Direct3D12.GetGpuDescriptorHandleForHeapStart(srvHeap);
        return true;
    }

    private bool CreateRenderTargets(nint swapChain)
    {
        renderTargets = new nint[bufferCount];
        nuint rtvBase = Direct3D12.GetCpuDescriptorHandleForHeapStart(rtvHeap);

        for (uint i = 0; i < bufferCount; i++)
        {
            int hr = Direct3D12.GetBuffer(swapChain, i, Direct3D12.IID_ID3D12Resource, out nint backBuffer);
            if (hr < 0 || backBuffer == 0)
            {
                OverlayLog.Write($"Compositor: GetBuffer({i}) failed, 0x{hr:X8}.");
                return false;
            }

            renderTargets[i] = backBuffer;
            Direct3D12.CreateRenderTargetView(device, backBuffer, rtvBase + (nuint)(i * rtvIncrement));
        }

        return true;
    }

    private bool CreateFrameResources()
    {
        commandAllocators = new nint[bufferCount];
        frameFenceValues = new ulong[bufferCount];

        for (uint i = 0; i < bufferCount; i++)
        {
            int hr = Direct3D12.CreateCommandAllocator(device, Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT, Direct3D12.IID_ID3D12CommandAllocator, out commandAllocators[i]);
            if (hr < 0 || commandAllocators[i] == 0)
            {
                OverlayLog.Write($"Compositor: CreateCommandAllocator({i}) failed, 0x{hr:X8}.");
                return false;
            }
        }

        int listHr = Direct3D12.CreateCommandList(device, 0, Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT, commandAllocators[0], 0, Direct3D12.IID_ID3D12GraphicsCommandList, out commandList);
        if (listHr < 0 || commandList == 0)
        {
            OverlayLog.Write($"Compositor: CreateCommandList failed, 0x{listHr:X8}.");
            return false;
        }

        // A command list is created open; close it so the first frame's Reset is legal.
        Direct3D12.CommandListClose(commandList);

        int fenceHr = Direct3D12.CreateFence(device, 0, Direct3D12.D3D12_FENCE_FLAG_NONE, Direct3D12.IID_ID3D12Fence, out frameFence);
        if (fenceHr < 0 || frameFence == 0)
        {
            OverlayLog.Write($"Compositor: CreateFence failed, 0x{fenceHr:X8}.");
            return false;
        }

        fenceEvent = Win32.CreateEventW(0, 0, 0, null);
        if (fenceEvent == 0)
        {
            OverlayLog.Write("Compositor: CreateEventW failed.");
            return false;
        }

        return true;
    }

    private bool PublishRequest()
    {
        long luid = Direct3D12.GetAdapterLuid(device);

        try
        {
            requestMap = MemoryMappedFile.CreateOrOpen(OverlaySharedSurface.RequestName(session), sizeof(OverlaySharedSurface.RequestBlock));
            requestPtr = Acquire(requestMap, out requestView);
        }
        catch (Exception exception)
        {
            OverlayLog.WriteException("Compositor: creating the request block", exception);
            return false;
        }

        OverlaySharedSurface.RequestBlock* request = (OverlaySharedSurface.RequestBlock*)requestPtr;
        request->Width = width;
        request->Height = height;
        request->Format = format;
        request->AdapterLuidLow = (uint)(luid & 0xFFFFFFFF);
        request->AdapterLuidHigh = (int)(luid >> 32);
        request->Version = OverlaySharedSurface.SurfaceVersion;
        // Publish the state last, with a barrier, so a producer that sees Requested sees the rest too.
        Thread.MemoryBarrier();
        request->State = (uint)OverlaySharedSurface.RequestState.Requested;
        request->Magic = OverlaySharedSurface.Magic;

        OverlayLog.Write($"Compositor: published surface request on adapter LUID 0x{luid:X}.");
        return true;
    }

    /// <summary>Draw the overlay for this frame, if there is a live producer to draw from.</summary>
    internal void Composite(nint swapChain)
    {
        if (!Volatile.Read(ref ready))
            return;

        if (!surfacesOpened && !TryOpenSurfaces())
        {
            Interlocked.Increment(ref skipped);
            return;
        }

        OverlaySharedSurface.PublishBlock* publish = (OverlaySharedSurface.PublishBlock*)publishPtr;
        if (publish->Magic != OverlaySharedSurface.Magic || publish->Alive == 0)
        {
            Interlocked.Increment(ref skipped);
            return;
        }

        uint latest = publish->LatestIndex;
        ulong waitValue = publish->FenceValue;
        if (latest >= OverlaySharedSurface.SurfaceCount || sharedTextures[latest] == 0)
        {
            Interlocked.Increment(ref skipped);
            return;
        }

        uint frameIndex = Direct3D12.GetCurrentBackBufferIndex(swapChain);

        // Wait until the GPU has finished the last time we used this allocator, then reuse it.
        if (Direct3D12.GetFenceCompletedValue(frameFence) < frameFenceValues[frameIndex])
        {
            Direct3D12.FenceSetEventOnCompletion(frameFence, frameFenceValues[frameIndex], fenceEvent);
            Win32.WaitForSingleObject(fenceEvent, Win32.INFINITE);
        }

        if (Direct3D12.CommandAllocatorReset(commandAllocators[frameIndex]) < 0 ||
            Direct3D12.CommandListReset(commandList, commandAllocators[frameIndex], pipelineState) < 0)
        {
            Interlocked.Increment(ref skipped);
            return;
        }

        nint backBuffer = renderTargets[frameIndex];
        nuint rtvHandle = Direct3D12.GetCpuDescriptorHandleForHeapStart(rtvHeap) + (nuint)(frameIndex * rtvIncrement);

        Direct3D12.ResourceBarrierDesc barrier = new()
        {
            Type = Direct3D12.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
            Flags = 0,
            Resource = backBuffer,
            Subresource = unchecked((uint)Direct3D12.D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES),
            StateBefore = Direct3D12.D3D12_RESOURCE_STATE_PRESENT,
            StateAfter = Direct3D12.D3D12_RESOURCE_STATE_RENDER_TARGET,
        };
        Direct3D12.ResourceBarrier(commandList, &barrier, 1);

        Direct3D12.OMSetRenderTargets(commandList, &rtvHandle);

        nint heap = srvHeap;
        Direct3D12.SetDescriptorHeaps(commandList, &heap, 1);
        Direct3D12.SetGraphicsRootSignature(commandList, rootSignature);
        Direct3D12.SetGraphicsRootDescriptorTable(commandList, 0, srvGpuBase + latest * srvIncrement);

        // Full-screen quad in normalised device coordinates: the vertex shader lerps this rectangle by
        // the corner ids, so (-1,1)->(1,-1) covers the whole target.
        float* rect = stackalloc float[4] { -1f, 1f, 1f, -1f };
        Direct3D12.SetGraphicsRoot32BitConstants(commandList, 1, 4, rect, 0);

        // Colour adjust for the pixel shader: brightness, PQ-encode flag, and two reserved slots.
        float* colorAdjust = stackalloc float[4] { brightness, pqEncode ? 1f : 0f, 0f, 0f };
        Direct3D12.SetGraphicsRoot32BitConstants(commandList, 2, 4, colorAdjust, 0);

        Direct3D12.Viewport viewport = new() { TopLeftX = 0, TopLeftY = 0, Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
        Direct3D12.RSSetViewports(commandList, &viewport, 1);

        Direct3D12.Rect scissor = new() { Left = 0, Top = 0, Right = (int)width, Bottom = (int)height };
        Direct3D12.RSSetScissorRects(commandList, &scissor, 1);

        Direct3D12.IASetPrimitiveTopology(commandList, Direct3D12.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        Direct3D12.DrawInstanced(commandList, 4, 1, 0, 0);

        barrier.StateBefore = Direct3D12.D3D12_RESOURCE_STATE_RENDER_TARGET;
        barrier.StateAfter = Direct3D12.D3D12_RESOURCE_STATE_PRESENT;
        Direct3D12.ResourceBarrier(commandList, &barrier, 1);

        if (Direct3D12.CommandListClose(commandList) < 0)
        {
            Interlocked.Increment(ref skipped);
            return;
        }

        // Order the producer's writes before our read on the GPU: the queue waits the shared fence
        // before it runs our list, so the render thread never blocks on the producer.
        Direct3D12.QueueWait(queue, sharedFence, waitValue);

        nint list = commandList;
        Direct3D12.ExecuteCommandLists(queue, 1, &list);

        ulong signalled = ++fenceValue;
        Direct3D12.QueueSignal(queue, frameFence, signalled);
        frameFenceValues[frameIndex] = signalled;

        Interlocked.Increment(ref composited);
    }

    private bool TryOpenSurfaces()
    {
        if (publishMap is null)
        {
            try
            {
                publishMap = MemoryMappedFile.OpenExisting(OverlaySharedSurface.PublishName(session));
                publishPtr = Acquire(publishMap, out publishView);
            }
            catch (FileNotFoundException)
            {
                return false; // No producer yet; try again next frame.
            }
            catch (Exception exception)
            {
                OverlayLog.WriteException("Compositor: opening the publish block", exception);
                return false;
            }
        }

        OverlaySharedSurface.PublishBlock* publish = (OverlaySharedSurface.PublishBlock*)publishPtr;
        if (publish->Magic != OverlaySharedSurface.Magic || publish->Alive == 0)
            return false;

        for (int i = 0; i < OverlaySharedSurface.SurfaceCount; i++)
        {
            if (!OpenSharedTexture(i))
                return false;
        }

        if (!OpenSharedFence())
            return false;

        surfacesOpened = true;
        OverlayLog.Write("Compositor: opened the producer's shared textures and fence; compositing.");
        return true;
    }

    private bool OpenSharedTexture(int index)
    {
        int hr = Direct3D12.OpenSharedHandleByName(device, OverlaySharedSurface.TextureName(session, index), out nint ntHandle);
        if (hr < 0 || ntHandle == 0)
        {
            OverlayLog.Write($"Compositor: OpenSharedHandleByName (texture {index}) failed, 0x{hr:X8}.");
            return false;
        }

        try
        {
            hr = Direct3D12.OpenSharedHandle(device, ntHandle, Direct3D12.IID_ID3D12Resource, out sharedTextures[index]);
        }
        finally
        {
            Win32.CloseHandle(ntHandle);
        }

        if (hr < 0 || sharedTextures[index] == 0)
        {
            OverlayLog.Write($"Compositor: OpenSharedHandle (texture {index}) failed, 0x{hr:X8}.");
            return false;
        }

        nuint srvHandle = Direct3D12.GetCpuDescriptorHandleForHeapStart(srvHeap) + (nuint)(index * srvIncrement);
        Direct3D12.CreateShaderResourceView(device, sharedTextures[index], srvHandle);
        return true;
    }

    private bool OpenSharedFence()
    {
        int hr = Direct3D12.OpenSharedHandleByName(device, OverlaySharedSurface.FenceName(session), out nint ntHandle);
        if (hr < 0 || ntHandle == 0)
        {
            OverlayLog.Write($"Compositor: OpenSharedHandleByName (fence) failed, 0x{hr:X8}.");
            return false;
        }

        try
        {
            hr = Direct3D12.OpenSharedHandle(device, ntHandle, Direct3D12.IID_ID3D12Fence, out sharedFence);
        }
        finally
        {
            Win32.CloseHandle(ntHandle);
        }

        if (hr < 0 || sharedFence == 0)
        {
            OverlayLog.Write($"Compositor: OpenSharedHandle (fence) failed, 0x{hr:X8}.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tear down the back-buffer-dependent resources ahead of a <c>ResizeBuffers</c>, so the game can
    /// release its buffers, then rebuild on the next <c>Present</c>.
    /// </summary>
    /// <remarks>
    /// The shared surfaces are the producer's and survive a resize; only the game's render targets,
    /// the heaps sized to them and the per-buffer allocators are torn down here. The pipeline itself
    /// is rebuilt too, because the back buffer format can change across a resize and the PSO is bound
    /// to it. A GPU drain first, or the game's <c>ResizeBuffers</c> fails with its buffers still
    /// referenced by our pending lists.
    /// </remarks>
    internal void PrepareForResize()
    {
        if (!Volatile.Read(ref ready))
            return;

        Volatile.Write(ref ready, false);
        DrainGpu();
        ReleaseDeviceResources();
        OverlayLog.Write("Compositor: released resources for ResizeBuffers; will rebuild on the next Present.");
    }

    /// <summary>Rebuild after a resize. Called on the first <c>Present</c> after <see cref="PrepareForResize"/>.</summary>
    internal bool Reinitialize(nint swapChain)
    {
        int hr = Direct3D12.GetSwapChainDesc(swapChain, out Direct3D12.DxgiSwapChainDesc desc);
        if (hr < 0)
            return false;

        bufferCount = desc.BufferCount;
        width = desc.BufferDesc.Width;
        height = desc.BufferDesc.Height;
        format = desc.BufferDesc.Format;

        if (!ChooseColorAdjust(format, out brightness, out pqEncode))
        {
            OverlayLog.Write($"Compositor: back buffer format {format} is not composable after resize; not compositing.");
            return false;
        }

        if (!CreatePipeline() || !CreateHeaps() || !CreateRenderTargets(swapChain) || !CreateFrameResources())
        {
            ReleaseDeviceResources();
            return false;
        }

        // The surfaces are reopened so their SRVs land in the freshly created heap.
        surfacesOpened = false;
        for (int i = 0; i < sharedTextures.Length; i++)
        {
            Direct3D12.Release(sharedTextures[i]);
            sharedTextures[i] = 0;
        }
        Direct3D12.Release(sharedFence);
        sharedFence = 0;

        Volatile.Write(ref ready, true);
        OverlayLog.Write($"Compositor: rebuilt for {width}x{height}, format {format}.");
        return true;
    }

    private void DrainGpu()
    {
        if (queue == 0 || frameFence == 0)
            return;

        ulong value = ++fenceValue;
        if (Direct3D12.QueueSignal(queue, frameFence, value) < 0)
            return;

        if (Direct3D12.GetFenceCompletedValue(frameFence) < value)
        {
            Direct3D12.FenceSetEventOnCompletion(frameFence, value, fenceEvent);
            Win32.WaitForSingleObject(fenceEvent, 5000);
        }
    }

    private void ReleaseDeviceResources()
    {
        foreach (nint rt in renderTargets)
            Direct3D12.Release(rt);
        renderTargets = [];

        foreach (nint allocator in commandAllocators)
            Direct3D12.Release(allocator);
        commandAllocators = [];
        frameFenceValues = [];

        Direct3D12.Release(commandList);
        commandList = 0;
        Direct3D12.Release(pipelineState);
        pipelineState = 0;
        Direct3D12.Release(rootSignature);
        rootSignature = 0;
        Direct3D12.Release(rtvHeap);
        rtvHeap = 0;
        Direct3D12.Release(srvHeap);
        srvHeap = 0;
        Direct3D12.Release(frameFence);
        frameFence = 0;
    }

    private void Shutdown()
    {
        Volatile.Write(ref ready, false);
        DrainGpu();
        ReleaseDeviceResources();

        for (int i = 0; i < sharedTextures.Length; i++)
        {
            Direct3D12.Release(sharedTextures[i]);
            sharedTextures[i] = 0;
        }
        Direct3D12.Release(sharedFence);
        sharedFence = 0;

        if (fenceEvent != 0)
        {
            Win32.CloseHandle(fenceEvent);
            fenceEvent = 0;
        }

        ReleaseView(ref requestMap, ref requestView, ref requestPtr);
        ReleaseView(ref publishMap, ref publishView, ref publishPtr);

        // The device and queue are the game's; we only ever borrowed them, so they are not released.
        device = 0;
        queue = 0;
    }

    public void Dispose() => Shutdown();

    /// <summary>
    /// Acquire a raw pointer into a mapping's view, keeping the accessor alive so the view is not
    /// unmapped under it.
    /// </summary>
    /// <remarks>
    /// A raw pointer rather than <c>MemoryMappedViewAccessor.Read&lt;T&gt;</c> because that method is
    /// annotated <c>[RequiresUnreferencedCode]</c> - it inspects the type at run time - and this
    /// assembly is linked into a trimmed, NativeAOT binary where that is a build error. The pointer is
    /// only valid while <paramref name="accessor"/> lives, which is why it is returned out and held.
    /// </remarks>
    private static byte* Acquire(MemoryMappedFile map, out MemoryMappedViewAccessor accessor)
    {
        accessor = map.CreateViewAccessor();
        byte* pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        return pointer;
    }

    private static void ReleaseView(ref MemoryMappedFile? map, ref MemoryMappedViewAccessor? accessor, ref byte* pointer)
    {
        if (accessor is not null)
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            accessor = null;
        }

        pointer = null;
        map?.Dispose();
        map = null;
    }
}
