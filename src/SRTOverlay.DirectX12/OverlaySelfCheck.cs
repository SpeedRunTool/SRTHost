using System.Runtime.Versioning;

namespace SRTOverlay.DirectX12;

/// <summary>
/// Exercises the risky Direct3D 12 interop in a throwaway process, so a wrong calling convention,
/// vtable slot or struct layout crashes the checker rather than a game.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a crash it would have caught: <see cref="Direct3D12.GetCommandQueueType"/>
/// calls <c>ID3D12CommandQueue::GetDesc</c>, which returns a sixteen-byte structure by value and so
/// uses the x64 hidden-return-pointer convention - and for a member function the hidden pointer is the
/// <i>second</i> argument, after <c>this</c>, not the first. Getting that backwards writes the
/// descriptor into the command queue's own memory. In a bare process that is a contained crash; inside
/// a game with Frame Generation wrapping the queue it corrupts state that surfaces as a fault
/// somewhere else entirely, which is exactly what happened. Every by-value or by-pointer COM call the
/// shim makes on a game's render thread is checked here first.
/// </para>
/// <para>
/// It creates its own device on the default adapter, so it needs a GPU (or the WARP software adapter)
/// but no game and nothing injected. It is run from the spike's <c>--selfcheck</c> mode. Nothing in
/// the shim's call graph reaches it, so the NativeAOT publish trims it out of the injected binary.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static unsafe class OverlaySelfCheck
{
    /// <summary>Run every check, logging each. Returns <see langword="false"/> on the first failure.</summary>
    /// <param name="log">Where each step reports.</param>
    /// <param name="drawRoundTrip">
    /// Whether to run the full producer-to-compositor draw round-trip. It spins a producer thread and
    /// does real GPU work, so the by-hand spike runs it but the unit-test suite does not - there it
    /// would load the machine enough to perturb timing-sensitive tests running in parallel.
    /// </param>
    public static bool Run(Action<string> log, bool drawRoundTrip = true)
    {
        nint factory = 0, adapter = 0, device = 0;
        nint directQueue = 0, copyQueue = 0, computeQueue = 0;

        try
        {
            if (Direct3D12.CreateDXGIFactory1(Direct3D12.IID_IDXGIFactory4, out factory) < 0 || factory == 0)
                return Fail(log, "CreateDXGIFactory1");

            if (Direct3D12.D3D12CreateDevice(0, Direct3D12.D3D_FEATURE_LEVEL_11_0, Direct3D12.IID_ID3D12Device, out device) < 0 || device == 0)
                return Fail(log, "D3D12CreateDevice (default adapter)");

            log("device created.");

            // The check that matters most: GetDesc's member-function sret convention. A DIRECT, a COPY
            // and a COMPUTE queue must each report their own type. A wrong argument order corrupts the
            // queue and the values come back wrong or the process dies here.
            if (!MakeQueue(device, Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT, out directQueue, log, "DIRECT") ||
                !MakeQueue(device, Direct3D12.D3D12_COMMAND_LIST_TYPE_COPY, out copyQueue, log, "COPY") ||
                !MakeQueue(device, 2 /* COMPUTE */, out computeQueue, log, "COMPUTE"))
            {
                return false;
            }

            int directType = Direct3D12.GetCommandQueueType(directQueue);
            int copyType = Direct3D12.GetCommandQueueType(copyQueue);
            int computeType = Direct3D12.GetCommandQueueType(computeQueue);
            log($"GetCommandQueueType: DIRECT->{directType}, COPY->{copyType}, COMPUTE->{computeType} (expect 0, 3, 2).");

            if (directType != Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT ||
                copyType != Direct3D12.D3D12_COMMAND_LIST_TYPE_COPY ||
                computeType != 2)
            {
                return Fail(log, "GetCommandQueueType returned the wrong type - the GetDesc sret ABI is wrong");
            }

            // The descriptor-handle getters, also by-value returns (but eight bytes, so in RAX rather
            // than sret): a heap's start handles must be non-zero and its GPU handle present.
            if (!CheckDescriptorHandles(device, log))
                return false;

            // The pipeline the compositor builds: root signature from the real serializer, then the PSO
            // from the shipped bytecode. A bad struct layout or slot index fails one of these.
            if (!CheckPipeline(device, log))
                return false;

            // The cross-process mechanism itself, round-tripped within one process: create a shared
            // texture and fence, publish named handles, open them back by name.
            if (!CheckSharedHandles(device, log))
                return false;

            // The whole compositor init sequence against a real SDR swapchain - GetBuffer, render
            // targets, per-frame command resources, the surface request - which is the exact path that
            // runs inside a game after a DIRECT queue is captured. This is what would have caught a
            // resource-setup fault without injecting into anything.
            if (!CheckCompositorInit(directQueue, log, drawRoundTrip))
                return false;

            log("SELF-CHECK PASSED. Every checked interop path behaved.");
            return true;
        }
        catch (Exception exception)
        {
            log($"SELF-CHECK threw: {exception}");
            return false;
        }
        finally
        {
            Direct3D12.Release(computeQueue);
            Direct3D12.Release(copyQueue);
            Direct3D12.Release(directQueue);
            Direct3D12.Release(device);
            Direct3D12.Release(adapter);
            Direct3D12.Release(factory);
        }
    }

    private static bool MakeQueue(nint device, int type, out nint queue, Action<string> log, string label)
    {
        Direct3D12.D3D12CommandQueueDesc desc = new() { Type = type, Priority = 0, Flags = 0, NodeMask = 0 };
        if (Direct3D12.CreateCommandQueue(device, desc, Direct3D12.IID_ID3D12CommandQueue, out queue) < 0 || queue == 0)
            return Fail(log, $"CreateCommandQueue ({label})");
        return true;
    }

    private static bool CheckDescriptorHandles(nint device, Action<string> log)
    {
        Direct3D12.DescriptorHeapDesc desc = new()
        {
            Type = Direct3D12.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            NumDescriptors = 2,
            Flags = Direct3D12.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
            NodeMask = 0,
        };
        if (Direct3D12.CreateDescriptorHeap(device, desc, Direct3D12.IID_ID3D12DescriptorHeap, out nint heap) < 0 || heap == 0)
            return Fail(log, "CreateDescriptorHeap");

        try
        {
            // Guard the GetCPUDescriptorHandleForHeapStart ABI decision. The real getter uses sret; the
            // RAX form is kept only for this comparison. They must differ - the RAX form returns the
            // return pointer, a stack address - which proves sret is the right choice and would flag a
            // future runtime that changed it, rather than silently crashing CreateRenderTargetView.
            nuint viaSret = Direct3D12.GetCpuDescriptorHandleForHeapStart(heap);
            nuint viaRax = Direct3D12.GetCpuDescriptorHandleForHeapStartRax(heap);
            log($"CPU-handle ABI: sret-form=0x{viaSret:X}, RAX-form=0x{viaRax:X}.");
            if (viaSret == viaRax)
                return Fail(log, "the CPU descriptor handle ABI is not the expected sret form - CreateRenderTargetView would crash");

            ulong gpu = Direct3D12.GetGpuDescriptorHandleForHeapStart(heap);
            uint inc = Direct3D12.GetDescriptorHandleIncrementSize(device, Direct3D12.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
            log($"descriptor handles: cpu=0x{viaSret:X}, gpu=0x{gpu:X}, increment={inc}.");
            if (viaSret == 0 || gpu == 0 || inc == 0)
                return Fail(log, "a descriptor handle or increment came back zero");
            return true;
        }
        finally
        {
            Direct3D12.Release(heap);
        }
    }

    private static bool CheckPipeline(nint device, Action<string> log)
    {
        // A minimal root signature: just the SRV table and root constants the composite shader uses.
        Direct3D12.DescriptorRange srvRange = new()
        {
            RangeType = Direct3D12.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            NumDescriptors = 1,
        };
        Direct3D12.RootParameter* parameters = stackalloc Direct3D12.RootParameter[3];
        parameters[0] = default;
        parameters[0].ParameterType = Direct3D12.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[0].NumDescriptorRanges = 1;
        parameters[0].DescriptorRanges = (nint)(&srvRange);
        parameters[0].ShaderVisibility = Direct3D12.D3D12_SHADER_VISIBILITY_PIXEL;
        parameters[1] = default;
        parameters[1].ParameterType = Direct3D12.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[1].Constants_ShaderRegister = 0; // b0, vertex shader
        parameters[1].Constants_Num32BitValues = 4;
        parameters[1].ShaderVisibility = Direct3D12.D3D12_SHADER_VISIBILITY_VERTEX;
        parameters[2] = default;
        parameters[2].ParameterType = Direct3D12.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[2].Constants_ShaderRegister = 1; // b1, pixel shader colour adjust
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
            MaxLOD = 3.402823466e+38f,
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

        if (Direct3D12.D3D12SerializeRootSignature(&rootDesc, Direct3D12.D3D_ROOT_SIGNATURE_VERSION_1_0, out nint blob, out nint errorBlob) < 0 || blob == 0)
        {
            Direct3D12.Release(errorBlob);
            return Fail(log, "D3D12SerializeRootSignature");
        }

        nint rootSignature;
        try
        {
            if (Direct3D12.CreateRootSignature(device, Direct3D12.BlobGetBufferPointer(blob), Direct3D12.BlobGetBufferSize(blob), Direct3D12.IID_ID3D12RootSignature, out rootSignature) < 0 || rootSignature == 0)
                return Fail(log, "CreateRootSignature");
        }
        finally
        {
            Direct3D12.Release(blob);
            Direct3D12.Release(errorBlob);
        }

        try
        {
            fixed (byte* vs = CompositeShaders.VertexShader)
            fixed (byte* ps = CompositeShaders.PixelShader)
            {
                Direct3D12.GraphicsPipelineStateDesc pso = default;
                pso.RootSignature = rootSignature;
                pso.VS = new Direct3D12.ShaderBytecode { BytecodePointer = (nint)vs, BytecodeLength = (nuint)CompositeShaders.VertexShader.Length };
                pso.PS = new Direct3D12.ShaderBytecode { BytecodePointer = (nint)ps, BytecodeLength = (nuint)CompositeShaders.PixelShader.Length };
                pso.SampleMask = Direct3D12.D3D12_DEFAULT_SAMPLE_MASK;
                pso.BlendState.RenderTarget0 = new Direct3D12.RenderTargetBlendDesc
                {
                    BlendEnable = 1,
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
                };
                pso.PrimitiveTopologyType = Direct3D12.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
                pso.NumRenderTargets = 1;
                pso.RTVFormats.Format0 = Direct3D12.DXGI_FORMAT_R8G8B8A8_UNORM;
                pso.SampleDesc = new Direct3D12.DxgiSampleDesc { Count = 1, Quality = 0 };

                if (Direct3D12.CreateGraphicsPipelineState(device, &pso, Direct3D12.IID_ID3D12PipelineState, out nint pipeline) < 0 || pipeline == 0)
                    return Fail(log, "CreateGraphicsPipelineState - a struct layout or slot index is wrong");

                Direct3D12.Release(pipeline);
            }

            log("pipeline: root signature and pipeline state created from the shipped bytecode.");
            return true;
        }
        finally
        {
            Direct3D12.Release(rootSignature);
        }
    }

    private static bool CheckSharedHandles(nint device, Action<string> log)
    {
        const string name = @"Local\SRTOverlaySelfCheckTex";

        Direct3D12.HeapProperties heap = new()
        {
            Type = Direct3D12.D3D12_HEAP_TYPE_DEFAULT,
            CreationNodeMask = 1,
            VisibleNodeMask = 1,
        };
        Direct3D12.ResourceDesc desc = new()
        {
            Dimension = Direct3D12.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Width = 64,
            Height = 64,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Direct3D12.DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleDesc = new Direct3D12.DxgiSampleDesc { Count = 1, Quality = 0 },
            Layout = Direct3D12.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            Flags = Direct3D12.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
        };

        if (Direct3D12.CreateCommittedResource(device, heap, Direct3D12.D3D12_HEAP_FLAG_SHARED, desc, Direct3D12.D3D12_RESOURCE_STATE_COMMON, Direct3D12.IID_ID3D12Resource, out nint texture) < 0 || texture == 0)
            return Fail(log, "CreateCommittedResource (shared texture)");

        nint opened = 0;
        nint created = 0;
        try
        {
            if (Direct3D12.CreateSharedHandle(device, texture, name, out created) < 0 || created == 0)
                return Fail(log, "CreateSharedHandle");

            // The name stays resolvable only while the created handle is open; closing it here is what
            // made OpenSharedHandleByName fail the first time. Keep it until the open has happened.
            if (Direct3D12.OpenSharedHandleByName(device, name, out nint ntHandle) < 0 || ntHandle == 0)
                return Fail(log, "OpenSharedHandleByName");
            try
            {
                if (Direct3D12.OpenSharedHandle(device, ntHandle, Direct3D12.IID_ID3D12Resource, out opened) < 0 || opened == 0)
                    return Fail(log, "OpenSharedHandle");
            }
            finally
            {
                Win32.CloseHandle(ntHandle);
            }

            log("shared handles: created a shared texture, published a named handle, opened it back.");
            return true;
        }
        finally
        {
            if (created != 0)
                Win32.CloseHandle(created);
            Direct3D12.Release(opened);
            Direct3D12.Release(texture);
        }
    }

    /// <summary>
    /// Run the compositor's full path against a real swapchain of each back-buffer format a game might
    /// present in - SDR 8-bit, 10-bit, and scRGB float - so every colour path is proven in-process.
    /// </summary>
    private static bool CheckCompositorInit(nint directQueue, Action<string> log, bool drawRoundTrip)
    {
        // Every flip-model back-buffer format the composite claims to handle. Creating a swapchain of
        // each succeeds on an ordinary SDR desktop (the format is independent of display HDR), so the
        // render-target, pipeline and colour-adjust paths are all exercised without an HDR display.
        (int format, string name)[] formats =
        [
            (Direct3D12.DXGI_FORMAT_R8G8B8A8_UNORM, "RGBA8 UNORM (SDR)"),
            (Direct3D12.DXGI_FORMAT_R10G10B10A2_UNORM, "R10G10B10A2 (10-bit)"),
            (Direct3D12.DXGI_FORMAT_R16G16B16A16_FLOAT, "R16F (scRGB HDR)"),
        ];

        for (int i = 0; i < formats.Length; i++)
        {
            if (!CheckOneFormat(directQueue, formats[i].format, formats[i].name, i, drawRoundTrip, log))
                return false;
        }

        return true;
    }

    private static bool CheckOneFormat(nint directQueue, int format, string label, int index, bool drawRoundTrip, Action<string> log)
    {
        nint factory = 0, swapChain1 = 0, swapChain3 = 0, window = 0;
        char* className = stackalloc char[24];
        string name = $"SRTOverlaySelfCheck{index}";
        name.AsSpan().CopyTo(new Span<char>(className, name.Length));
        className[name.Length] = '\0';
        nint module = Win32.GetModuleHandleW(null);
        string session = $"selfcheck{index}";

        try
        {
            if (Direct3D12.CreateDXGIFactory1(Direct3D12.IID_IDXGIFactory4, out factory) < 0 || factory == 0)
                return Fail(log, $"CreateDXGIFactory1 ({label})");

            Win32.WndClassExW wc = new()
            {
                cbSize = (uint)sizeof(Win32.WndClassExW),
                lpfnWndProc = Win32.GetProcAddress(Win32.GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                hInstance = module,
                lpszClassName = className,
            };
            if (Win32.RegisterClassExW(&wc) == 0)
                return Fail(log, $"RegisterClassExW ({label})");

            window = Win32.CreateWindowExW(0, className, className, 0, 0, 0, 256, 256, 0, 0, module, 0);
            if (window == 0)
            {
                Win32.UnregisterClassW(className, module);
                return Fail(log, $"CreateWindowExW ({label})");
            }

            Direct3D12.DxgiSwapChainDesc1 desc = new()
            {
                Width = 256,
                Height = 256,
                Format = format,
                SampleDesc = new Direct3D12.DxgiSampleDesc { Count = 1, Quality = 0 },
                BufferUsage = Direct3D12.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 3,
                Scaling = Direct3D12.DXGI_SCALING_STRETCH,
                SwapEffect = Direct3D12.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = Direct3D12.DXGI_ALPHA_MODE_UNSPECIFIED,
            };

            if (Direct3D12.CreateSwapChainForHwnd(factory, directQueue, window, desc, out swapChain1) < 0 || swapChain1 == 0)
                return Fail(log, $"CreateSwapChainForHwnd ({label})");

            if (Direct3D12.QueryInterface(swapChain1, Direct3D12.IID_IDXGISwapChain3, out swapChain3) < 0 || swapChain3 == 0)
                return Fail(log, $"QueryInterface IDXGISwapChain3 ({label})");

            using D3D12Compositor compositor = new(session);
            if (!compositor.Initialize(swapChain3, directQueue))
                return Fail(log, $"D3D12Compositor.Initialize failed for {label}");

            if (!drawRoundTrip)
            {
                log($"compositor [{label}]: init succeeded (draw round-trip skipped, light mode).");
                return true;
            }

            // The full draw-and-submit: stand up the producer (reads the request, creates the shared
            // surface, animates), then pump Composite + Present until a frame is genuinely drawn. This
            // exercises command-list recording, the queue-side fence wait, ExecuteCommandLists, Signal
            // and the format's colour-adjust path end to end.
            using CancellationTokenSource producerStop = new();
            SharedSurfaceProducer producer = new(session, _ => { });
            Thread producerThread = new(() => producer.Run(producerStop.Token)) { IsBackground = true, Name = $"self-check producer {index}" };
            producerThread.Start();

            try
            {
                for (int i = 0; i < 400 && compositor.CompositedFrames == 0; i++)
                {
                    compositor.Composite(swapChain3);
                    Direct3D12.Present(swapChain3, 0, 0);
                    Thread.Sleep(5);
                }
            }
            finally
            {
                producerStop.Cancel();
                producerThread.Join(2000);
            }

            if (compositor.CompositedFrames == 0)
                return Fail(log, $"the compositor never drew a frame for {label} within the timeout");

            log($"compositor [{label}]: composited {compositor.CompositedFrames} real frame(s) - full path proven.");
            return true;
        }
        finally
        {
            Direct3D12.Release(swapChain3);
            Direct3D12.Release(swapChain1);
            Direct3D12.Release(factory);
            if (window != 0)
                Win32.DestroyWindow(window);
            Win32.UnregisterClassW(className, module);
        }
    }

    private static bool Fail(Action<string> log, string what)
    {
        log($"SELF-CHECK FAILED: {what}.");
        return false;
    }
}
