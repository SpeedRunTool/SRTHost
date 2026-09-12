#include "D3D12Compositor.h"

#include <cstdio>

#include "CompositeShaders.h"
#include "OverlayLog.h"
#include "OverlayRuntime.h"
#include "OverlaySharedNames.h"

using Microsoft::WRL::ComPtr;
using srtoverlay::core::OverlayLog;
namespace names = srtoverlay::protocol;

namespace srtoverlay::directx12
{
    namespace
    {
        // Access mask shared handles are created and opened with. GENERIC_ALL is right for a same-user
        // producer and consumer; a narrower mask buys nothing when both are the same principal.
        constexpr DWORD kSharedAccess = 0x10000000; // GENERIC_ALL
    }

    D3D12Compositor::D3D12Compositor(std::wstring session)
        : session_(std::move(session))
    {
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        ticksPerSecond_ = frequency.QuadPart != 0 ? frequency.QuadPart : 1;
    }

    D3D12Compositor::~D3D12Compositor()
    {
        Shutdown();
    }

    bool D3D12Compositor::ChooseColorAdjust(int format, float& brightness, bool& pqEncode) const
    {
        brightness = 1.0f;
        pqEncode = false;

        switch (format)
        {
            case DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
            case DXGI_FORMAT_B8G8R8A8_UNORM:
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
            case DXGI_FORMAT_R10G10B10A2_UNORM:
                break;
            case DXGI_FORMAT_R16G16B16A16_FLOAT:
                brightness = 200.0f / 80.0f; // scRGB reference white
                break;
            default:
                return false;
        }

        // Live overrides from the startup options: flip a 10-bit buffer to PQ, or change the reference
        // white, without rebuilding the shim.
        if (const SrtOverlayStartupOptions* options = core::OverlayRuntime::Options())
        {
            if (options->hasBrightness && options->overlayBrightness > 0.0f)
                brightness = options->overlayBrightness;
            if (options->forcePq)
                pqEncode = true;
        }

        return true;
    }

    bool D3D12Compositor::Initialize(IDXGISwapChain3* swapChain, ID3D12CommandQueue* commandQueue)
    {
        queue_ = commandQueue;

        HRESULT hr = swapChain->GetDevice(IID_PPV_ARGS(&device_));
        if (FAILED(hr) || !device_)
        {
            OverlayLog::WriteHResult("Compositor: GetDevice", hr);
            return false;
        }

        DXGI_SWAP_CHAIN_DESC desc{};
        hr = swapChain->GetDesc(&desc);
        if (FAILED(hr))
        {
            OverlayLog::WriteHResult("Compositor: GetDesc", hr);
            return false;
        }

        bufferCount_ = desc.BufferCount;
        width_ = desc.BufferDesc.Width;
        height_ = desc.BufferDesc.Height;
        format_ = static_cast<int>(desc.BufferDesc.Format);

        {
            char line[160];
            std::snprintf(line, sizeof(line), "Compositor: back buffer %ux%u, format %d, %u buffers.",
                          width_, height_, format_, bufferCount_);
            OverlayLog::Write(line);
        }

        if (!ChooseColorAdjust(format_, brightness_, pqEncode_))
        {
            OverlayLog::Write("Compositor: back buffer format is not one the composite handles; not "
                              "compositing. The hooks stay installed and pass through.");
            return false;
        }

        {
            char line[128];
            std::snprintf(line, sizeof(line), "Compositor: colour adjust for format %d: brightness %.3f, PQ-encode %d.",
                          format_, brightness_, pqEncode_ ? 1 : 0);
            OverlayLog::Write(line);
        }

        if (!CreatePipeline() || !CreateHeaps() || !CreateRenderTargets(swapChain) ||
            !CreateFrameResources() || !PublishRequest())
        {
            Shutdown();
            return false;
        }

        ready_.store(true);
        OverlayLog::Write("Compositor: initialised; waiting for a producer to create the surface.");
        return true;
    }

    bool D3D12Compositor::CreatePipeline()
    {
        D3D12_DESCRIPTOR_RANGE srvRange{};
        srvRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRange.NumDescriptors = 1;
        srvRange.BaseShaderRegister = 0;
        srvRange.OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;

        D3D12_ROOT_PARAMETER parameters[3]{};
        parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[0].DescriptorTable.NumDescriptorRanges = 1;
        parameters[0].DescriptorTable.pDescriptorRanges = &srvRange;
        parameters[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[1].Constants.ShaderRegister = 0; // b0: placement rectangle (vertex shader)
        parameters[1].Constants.Num32BitValues = 4;
        parameters[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_VERTEX;

        parameters[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        parameters[2].Constants.ShaderRegister = 1; // b1: colour adjust (pixel shader)
        parameters[2].Constants.Num32BitValues = 4;
        parameters[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC sampler{};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
        sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_ALWAYS;
        sampler.BorderColor = D3D12_STATIC_BORDER_COLOR_TRANSPARENT_BLACK;
        sampler.MaxLOD = D3D12_FLOAT32_MAX;
        sampler.ShaderRegister = 0;
        sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_ROOT_SIGNATURE_DESC rootDesc{};
        rootDesc.NumParameters = 3;
        rootDesc.pParameters = parameters;
        rootDesc.NumStaticSamplers = 1;
        rootDesc.pStaticSamplers = &sampler;
        rootDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;

        ComPtr<ID3DBlob> blob;
        ComPtr<ID3DBlob> error;
        HRESULT hr = D3D12SerializeRootSignature(&rootDesc, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &error);
        if (FAILED(hr) || !blob)
        {
            OverlayLog::WriteHResult("Compositor: D3D12SerializeRootSignature", hr);
            return false;
        }

        hr = device_->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&rootSignature_));
        if (FAILED(hr) || !rootSignature_)
        {
            OverlayLog::WriteHResult("Compositor: CreateRootSignature", hr);
            return false;
        }

        D3D12_GRAPHICS_PIPELINE_STATE_DESC pso{};
        pso.pRootSignature = rootSignature_.Get();
        pso.VS = {g_CompositeVertexShader, sizeof(g_CompositeVertexShader)};
        pso.PS = {g_CompositePixelShader, sizeof(g_CompositePixelShader)};
        pso.SampleMask = UINT_MAX;

        // Premultiplied-alpha over the game's frame: source is already multiplied by its alpha, so the
        // blend is Src*ONE + Dst*(1-SrcA).
        D3D12_RENDER_TARGET_BLEND_DESC& rt = pso.BlendState.RenderTarget[0];
        rt.BlendEnable = TRUE;
        rt.LogicOpEnable = FALSE;
        rt.SrcBlend = D3D12_BLEND_ONE;
        rt.DestBlend = D3D12_BLEND_INV_SRC_ALPHA;
        rt.BlendOp = D3D12_BLEND_OP_ADD;
        rt.SrcBlendAlpha = D3D12_BLEND_ONE;
        rt.DestBlendAlpha = D3D12_BLEND_INV_SRC_ALPHA;
        rt.BlendOpAlpha = D3D12_BLEND_OP_ADD;
        rt.LogicOp = D3D12_LOGIC_OP_NOOP;
        rt.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

        pso.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
        pso.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
        pso.RasterizerState.DepthClipEnable = TRUE;
        pso.RasterizerState.ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF;

        // Depth and stencil off: the overlay draws over the finished frame and reads no depth.
        pso.DepthStencilState.DepthEnable = FALSE;
        pso.DepthStencilState.StencilEnable = FALSE;

        pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pso.NumRenderTargets = 1;
        pso.RTVFormats[0] = static_cast<DXGI_FORMAT>(format_);
        pso.SampleDesc.Count = 1;
        pso.Flags = D3D12_PIPELINE_STATE_FLAG_NONE;

        hr = device_->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&pipelineState_));
        if (FAILED(hr) || !pipelineState_)
        {
            OverlayLog::WriteHResult("Compositor: CreateGraphicsPipelineState", hr);
            return false;
        }

        return true;
    }

    bool D3D12Compositor::CreateHeaps()
    {
        D3D12_DESCRIPTOR_HEAP_DESC rtvDesc{};
        rtvDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        rtvDesc.NumDescriptors = bufferCount_;
        rtvDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

        HRESULT hr = device_->CreateDescriptorHeap(&rtvDesc, IID_PPV_ARGS(&rtvHeap_));
        if (FAILED(hr) || !rtvHeap_)
        {
            OverlayLog::WriteHResult("Compositor: CreateDescriptorHeap (RTV)", hr);
            return false;
        }

        D3D12_DESCRIPTOR_HEAP_DESC srvDesc{};
        srvDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        srvDesc.NumDescriptors = SRT_OVERLAY_SURFACE_COUNT;
        srvDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        hr = device_->CreateDescriptorHeap(&srvDesc, IID_PPV_ARGS(&srvHeap_));
        if (FAILED(hr) || !srvHeap_)
        {
            OverlayLog::WriteHResult("Compositor: CreateDescriptorHeap (SRV)", hr);
            return false;
        }

        rtvIncrement_ = device_->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        srvIncrement_ = device_->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        srvGpuBase_ = srvHeap_->GetGPUDescriptorHandleForHeapStart();
        return true;
    }

    bool D3D12Compositor::CreateRenderTargets(IDXGISwapChain3* swapChain)
    {
        renderTargets_.assign(bufferCount_, nullptr);
        D3D12_CPU_DESCRIPTOR_HANDLE rtvBase = rtvHeap_->GetCPUDescriptorHandleForHeapStart();

        for (UINT i = 0; i < bufferCount_; ++i)
        {
            HRESULT hr = swapChain->GetBuffer(i, IID_PPV_ARGS(&renderTargets_[i]));
            if (FAILED(hr) || !renderTargets_[i])
            {
                OverlayLog::WriteHResult("Compositor: GetBuffer", hr);
                return false;
            }

            D3D12_CPU_DESCRIPTOR_HANDLE handle = rtvBase;
            handle.ptr += static_cast<SIZE_T>(i) * rtvIncrement_;
            device_->CreateRenderTargetView(renderTargets_[i].Get(), nullptr, handle);
        }

        return true;
    }

    bool D3D12Compositor::CreateFrameResources()
    {
        commandAllocators_.assign(bufferCount_, nullptr);
        frameFenceValues_.assign(bufferCount_, 0);

        for (UINT i = 0; i < bufferCount_; ++i)
        {
            HRESULT hr = device_->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&commandAllocators_[i]));
            if (FAILED(hr) || !commandAllocators_[i])
            {
                OverlayLog::WriteHResult("Compositor: CreateCommandAllocator", hr);
                return false;
            }
        }

        HRESULT hr = device_->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, commandAllocators_[0].Get(), nullptr, IID_PPV_ARGS(&commandList_));
        if (FAILED(hr) || !commandList_)
        {
            OverlayLog::WriteHResult("Compositor: CreateCommandList", hr);
            return false;
        }

        // A command list is created open; close it so the first frame's Reset is legal.
        commandList_->Close();

        hr = device_->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&frameFence_));
        if (FAILED(hr) || !frameFence_)
        {
            OverlayLog::WriteHResult("Compositor: CreateFence", hr);
            return false;
        }

        fenceEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (fenceEvent_ == nullptr)
        {
            OverlayLog::Write("Compositor: CreateEventW failed.");
            return false;
        }

        return true;
    }

    bool D3D12Compositor::PublishRequest()
    {
        const LUID luid = device_->GetAdapterLuid();

        const std::wstring requestName = names::RequestName(session_);
        requestMap_ = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                         sizeof(SrtOverlayRequestBlock), requestName.c_str());
        if (requestMap_ == nullptr)
        {
            OverlayLog::Write("Compositor: CreateFileMapping (request) failed.");
            return false;
        }

        request_ = static_cast<SrtOverlayRequestBlock*>(
            MapViewOfFile(requestMap_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(SrtOverlayRequestBlock)));
        if (request_ == nullptr)
        {
            OverlayLog::Write("Compositor: MapViewOfFile (request) failed.");
            return false;
        }

        request_->width = width_;
        request_->height = height_;
        request_->format = format_;
        request_->adapterLuidLow = static_cast<uint32_t>(luid.LowPart);
        request_->adapterLuidHigh = static_cast<int32_t>(luid.HighPart);
        request_->version = SRT_OVERLAY_SURFACE_VERSION;
        // Publish state last, with a barrier, so a producer that sees Requested sees the rest too.
        std::atomic_thread_fence(std::memory_order_seq_cst);
        request_->state = SrtOverlayRequestState_Requested;
        request_->magic = SRT_OVERLAY_SURFACE_MAGIC;

        char line[96];
        std::snprintf(line, sizeof(line), "Compositor: published surface request on adapter LUID 0x%08X%08X.",
                      static_cast<unsigned>(luid.HighPart), static_cast<unsigned>(luid.LowPart));
        OverlayLog::Write(line);
        return true;
    }

    void D3D12Compositor::Composite(IDXGISwapChain3* swapChain)
    {
        if (!ready_.load())
            return;

        if (!surfacesOpened_ && !TryOpenSurfaces())
        {
            skipped_.fetch_add(1);
            return;
        }

        if (publish_->magic != SRT_OVERLAY_SURFACE_MAGIC || publish_->alive == 0)
        {
            skipped_.fetch_add(1);
            return;
        }

        const UINT latest = publish_->latestIndex;
        const UINT64 waitValue = publish_->fenceValue;
        if (latest >= SRT_OVERLAY_SURFACE_COUNT || !sharedTextures_[latest])
        {
            skipped_.fetch_add(1);
            return;
        }

        LARGE_INTEGER start{};
        QueryPerformanceCounter(&start);

        const UINT frameIndex = swapChain->GetCurrentBackBufferIndex();

        // Wait until the GPU has finished the last time we used this allocator, then reuse it.
        if (frameFence_->GetCompletedValue() < frameFenceValues_[frameIndex])
        {
            frameFence_->SetEventOnCompletion(frameFenceValues_[frameIndex], fenceEvent_);
            WaitForSingleObject(fenceEvent_, INFINITE);
        }

        if (FAILED(commandAllocators_[frameIndex]->Reset()) ||
            FAILED(commandList_->Reset(commandAllocators_[frameIndex].Get(), pipelineState_.Get())))
        {
            skipped_.fetch_add(1);
            return;
        }

        ID3D12Resource* backBuffer = renderTargets_[frameIndex].Get();
        D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = rtvHeap_->GetCPUDescriptorHandleForHeapStart();
        rtvHandle.ptr += static_cast<SIZE_T>(frameIndex) * rtvIncrement_;

        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = backBuffer;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        commandList_->ResourceBarrier(1, &barrier);

        commandList_->OMSetRenderTargets(1, &rtvHandle, FALSE, nullptr);

        ID3D12DescriptorHeap* heaps[] = {srvHeap_.Get()};
        commandList_->SetDescriptorHeaps(1, heaps);
        commandList_->SetGraphicsRootSignature(rootSignature_.Get());

        D3D12_GPU_DESCRIPTOR_HANDLE srvHandle = srvGpuBase_;
        srvHandle.ptr += static_cast<UINT64>(latest) * srvIncrement_;
        commandList_->SetGraphicsRootDescriptorTable(0, srvHandle);

        // Full-screen quad in NDC: the vertex shader lerps this rectangle by the corner ids.
        const float rect[4] = {-1.0f, 1.0f, 1.0f, -1.0f};
        commandList_->SetGraphicsRoot32BitConstants(1, 4, rect, 0);

        const float colorAdjust[4] = {brightness_, pqEncode_ ? 1.0f : 0.0f, 0.0f, 0.0f};
        commandList_->SetGraphicsRoot32BitConstants(2, 4, colorAdjust, 0);

        D3D12_VIEWPORT viewport{};
        viewport.Width = static_cast<float>(width_);
        viewport.Height = static_cast<float>(height_);
        viewport.MaxDepth = 1.0f;
        commandList_->RSSetViewports(1, &viewport);

        D3D12_RECT scissor{0, 0, static_cast<LONG>(width_), static_cast<LONG>(height_)};
        commandList_->RSSetScissorRects(1, &scissor);

        commandList_->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        commandList_->DrawInstanced(4, 1, 0, 0);

        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        commandList_->ResourceBarrier(1, &barrier);

        if (FAILED(commandList_->Close()))
        {
            skipped_.fetch_add(1);
            return;
        }

        // Order the producer's writes before our read on the GPU: the queue waits the shared fence
        // before running our list, so the render thread never blocks on the producer.
        queue_->Wait(sharedFence_.Get(), waitValue);

        ID3D12CommandList* lists[] = {commandList_.Get()};
        queue_->ExecuteCommandLists(1, lists);

        const UINT64 signalled = ++fenceValue_;
        queue_->Signal(frameFence_.Get(), signalled);
        frameFenceValues_[frameIndex] = signalled;

        LARGE_INTEGER end{};
        QueryPerformanceCounter(&end);
        RecordCost(end.QuadPart - start.QuadPart);
        composited_.fetch_add(1);
    }

    void D3D12Compositor::RecordCost(int64_t elapsedTicks)
    {
        const long us = static_cast<long>(elapsedTicks * 1000000LL / ticksPerSecond_);
        ++costCount_;
        costSumUs_ += us;
        if (us > costMaxUs_)
            costMaxUs_ = us;

        int bucket = static_cast<int>(us / kCostBucketUs);
        if (bucket >= kCostBuckets)
            bucket = kCostBuckets - 1;
        if (bucket < 0)
            bucket = 0;
        ++costHistogram_[bucket];
    }

    long D3D12Compositor::CostPercentileUs(double quantile) const
    {
        const long total = costCount_;
        if (total == 0)
            return 0;

        const long threshold = static_cast<long>(static_cast<double>(total) * quantile);
        long cumulative = 0;
        for (int i = 0; i < kCostBuckets; ++i)
        {
            cumulative += costHistogram_[i];
            if (cumulative >= threshold)
                return static_cast<long>(i) * kCostBucketUs;
        }
        return costMaxUs_;
    }

    bool D3D12Compositor::TryOpenSurfaces()
    {
        if (publishMap_ == nullptr)
        {
            const std::wstring publishName = names::PublishName(session_);
            publishMap_ = OpenFileMappingW(FILE_MAP_READ, FALSE, publishName.c_str());
            if (publishMap_ == nullptr)
                return false; // No producer yet; try again next frame.

            publish_ = static_cast<const SrtOverlayPublishBlock*>(
                MapViewOfFile(publishMap_, FILE_MAP_READ, 0, 0, sizeof(SrtOverlayPublishBlock)));
            if (publish_ == nullptr)
            {
                CloseHandle(publishMap_);
                publishMap_ = nullptr;
                return false;
            }
        }

        if (publish_->magic != SRT_OVERLAY_SURFACE_MAGIC || publish_->alive == 0)
            return false;

        for (int i = 0; i < SRT_OVERLAY_SURFACE_COUNT; ++i)
        {
            if (!OpenSharedTexture(i))
                return false;
        }

        if (!OpenSharedFence())
            return false;

        surfacesOpened_ = true;
        OverlayLog::Write("Compositor: opened the producer's shared textures and fence; compositing.");
        return true;
    }

    bool D3D12Compositor::OpenSharedTexture(int index)
    {
        const std::wstring textureName = names::TextureName(session_, index);
        HANDLE ntHandle = nullptr;
        HRESULT hr = device_->OpenSharedHandleByName(textureName.c_str(), kSharedAccess, &ntHandle);
        if (FAILED(hr) || ntHandle == nullptr)
        {
            OverlayLog::WriteHResult("Compositor: OpenSharedHandleByName (texture)", hr);
            return false;
        }

        hr = device_->OpenSharedHandle(ntHandle, IID_PPV_ARGS(&sharedTextures_[index]));
        CloseHandle(ntHandle);

        if (FAILED(hr) || !sharedTextures_[index])
        {
            OverlayLog::WriteHResult("Compositor: OpenSharedHandle (texture)", hr);
            return false;
        }

        D3D12_CPU_DESCRIPTOR_HANDLE srvHandle = srvHeap_->GetCPUDescriptorHandleForHeapStart();
        srvHandle.ptr += static_cast<SIZE_T>(index) * srvIncrement_;
        device_->CreateShaderResourceView(sharedTextures_[index].Get(), nullptr, srvHandle);
        return true;
    }

    bool D3D12Compositor::OpenSharedFence()
    {
        const std::wstring fenceName = names::FenceName(session_);
        HANDLE ntHandle = nullptr;
        HRESULT hr = device_->OpenSharedHandleByName(fenceName.c_str(), kSharedAccess, &ntHandle);
        if (FAILED(hr) || ntHandle == nullptr)
        {
            OverlayLog::WriteHResult("Compositor: OpenSharedHandleByName (fence)", hr);
            return false;
        }

        hr = device_->OpenSharedHandle(ntHandle, IID_PPV_ARGS(&sharedFence_));
        CloseHandle(ntHandle);

        if (FAILED(hr) || !sharedFence_)
        {
            OverlayLog::WriteHResult("Compositor: OpenSharedHandle (fence)", hr);
            return false;
        }

        return true;
    }

    void D3D12Compositor::PrepareForResize()
    {
        if (!ready_.load())
            return;

        ready_.store(false);
        DrainGpu();
        ReleaseDeviceResources();
        OverlayLog::Write("Compositor: released resources for ResizeBuffers; will rebuild on the next Present.");
    }

    bool D3D12Compositor::Reinitialize(IDXGISwapChain3* swapChain)
    {
        DXGI_SWAP_CHAIN_DESC desc{};
        if (FAILED(swapChain->GetDesc(&desc)))
            return false;

        bufferCount_ = desc.BufferCount;
        width_ = desc.BufferDesc.Width;
        height_ = desc.BufferDesc.Height;
        format_ = static_cast<int>(desc.BufferDesc.Format);

        if (!ChooseColorAdjust(format_, brightness_, pqEncode_))
        {
            OverlayLog::Write("Compositor: back buffer format is not composable after resize; not compositing.");
            return false;
        }

        if (!CreatePipeline() || !CreateHeaps() || !CreateRenderTargets(swapChain) || !CreateFrameResources())
        {
            ReleaseDeviceResources();
            return false;
        }

        // Reopen the surfaces so their SRVs land in the freshly created heap.
        surfacesOpened_ = false;
        for (auto& texture : sharedTextures_)
            texture.Reset();
        sharedFence_.Reset();

        ready_.store(true);
        char line[96];
        std::snprintf(line, sizeof(line), "Compositor: rebuilt for %ux%u, format %d.", width_, height_, format_);
        OverlayLog::Write(line);
        return true;
    }

    void D3D12Compositor::DrainGpu()
    {
        if (queue_ == nullptr || !frameFence_)
            return;

        const UINT64 value = ++fenceValue_;
        if (FAILED(queue_->Signal(frameFence_.Get(), value)))
            return;

        if (frameFence_->GetCompletedValue() < value)
        {
            frameFence_->SetEventOnCompletion(value, fenceEvent_);
            WaitForSingleObject(fenceEvent_, 5000);
        }
    }

    void D3D12Compositor::ReleaseDeviceResources()
    {
        renderTargets_.clear();
        commandAllocators_.clear();
        frameFenceValues_.clear();
        commandList_.Reset();
        pipelineState_.Reset();
        rootSignature_.Reset();
        rtvHeap_.Reset();
        srvHeap_.Reset();
        frameFence_.Reset();
    }

    void D3D12Compositor::Shutdown()
    {
        ready_.store(false);
        DrainGpu();
        ReleaseDeviceResources();

        for (auto& texture : sharedTextures_)
            texture.Reset();
        sharedFence_.Reset();

        if (fenceEvent_ != nullptr)
        {
            CloseHandle(fenceEvent_);
            fenceEvent_ = nullptr;
        }

        if (request_ != nullptr)
        {
            UnmapViewOfFile(request_);
            request_ = nullptr;
        }
        if (requestMap_ != nullptr)
        {
            CloseHandle(requestMap_);
            requestMap_ = nullptr;
        }
        if (publish_ != nullptr)
        {
            UnmapViewOfFile(publish_);
            publish_ = nullptr;
        }
        if (publishMap_ != nullptr)
        {
            CloseHandle(publishMap_);
            publishMap_ = nullptr;
        }

        // The device and queue are the game's; we only ever borrowed them, so they are not released
        // here beyond dropping our reference to the device.
        device_.Reset();
        queue_ = nullptr;
    }

    std::string D3D12Compositor::DescribeActivity() const
    {
        char buffer[256];
        if (costCount_ > 0)
        {
            std::snprintf(buffer, sizeof(buffer),
                          "composited %ld, skipped %ld, surfaces %s, cost/frame avg %ldus p50 %ldus p99 %ldus max %ldus",
                          composited_.load(), skipped_.load(), surfacesOpened_ ? "open" : "waiting for producer",
                          costSumUs_ / costCount_, CostPercentileUs(0.50), CostPercentileUs(0.99), costMaxUs_);
        }
        else
        {
            std::snprintf(buffer, sizeof(buffer), "composited %ld, skipped %ld, surfaces %s",
                          composited_.load(), skipped_.load(), surfacesOpened_ ? "open" : "waiting for producer");
        }
        return buffer;
    }
}
