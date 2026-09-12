// Draws one textured quad into the game's back buffer each frame - a texture another process rendered
// into shared memory on the game's own adapter. This is the whole of what the shim draws.
//
// The shim owns no picture of its own: it opens a texture and a fence the producer created with
// D3D12_HEAP_FLAG_SHARED / D3D12_FENCE_FLAG_SHARED, waits the fence on the game's queue so the GPU
// orders the two processes' work without the render thread ever blocking, and composites one quad with
// premultiplied alpha. Everything expressive happens in the producer's process; a bug there crashes
// the producer, not the game.
//
// NOTHING here allocates on the steady-state Present path. The heaps, pipeline, per-buffer allocators,
// descriptors and shared handles are built once (and rebuilt on a resize). A composited frame is a
// handful of D3D12 calls and no heap allocation, which is what the under-a-millisecond budget needs.
// It is not reentrant: it only ever runs on the game's render thread, inside the hooked Present.

#ifndef SRTOVERLAY_D3D12_COMPOSITOR_H
#define SRTOVERLAY_D3D12_COMPOSITOR_H

#include <windows.h>

#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdint>
#include <string>
#include <vector>

#include "OverlayProtocol.h"

namespace srtoverlay::directx12
{
    class D3D12Compositor
    {
    public:
        explicit D3D12Compositor(std::wstring session);
        ~D3D12Compositor();

        D3D12Compositor(const D3D12Compositor&) = delete;
        D3D12Compositor& operator=(const D3D12Compositor&) = delete;

        // Build every device resource and publish the surface request. Called once, on the render
        // thread, after Present has handed us the swapchain and a DIRECT queue. Returns false on any
        // failure; the caller then composites nothing.
        bool Initialize(IDXGISwapChain3* swapChain, ID3D12CommandQueue* commandQueue);

        // Draw the overlay for this frame, if there is a live producer to draw from.
        void Composite(IDXGISwapChain3* swapChain);

        // Tear down the back-buffer-dependent resources before a ResizeBuffers; rebuild on the next
        // Present via Reinitialize.
        void PrepareForResize();
        bool Reinitialize(IDXGISwapChain3* swapChain);

        bool IsReady() const { return ready_.load(); }
        long CompositedFrames() const { return composited_.load(); }
        std::string DescribeActivity() const;

    private:
        bool ChooseColorAdjust(int format, float& brightness, bool& pqEncode) const;
        bool CreatePipeline();
        bool CreateHeaps();
        bool CreateRenderTargets(IDXGISwapChain3* swapChain);
        bool CreateFrameResources();
        bool PublishRequest();
        bool TryOpenSurfaces();
        bool OpenSharedTexture(int index);
        bool OpenSharedFence();
        void DrainGpu();
        void ReleaseDeviceResources();
        void Shutdown();
        void RecordCost(int64_t elapsedTicks);
        long CostPercentileUs(double quantile) const;

        std::wstring session_;

        Microsoft::WRL::ComPtr<ID3D12Device> device_;
        ID3D12CommandQueue* queue_ = nullptr; // the game's queue; borrowed, never released

        Microsoft::WRL::ComPtr<ID3D12RootSignature> rootSignature_;
        Microsoft::WRL::ComPtr<ID3D12PipelineState> pipelineState_;
        Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> rtvHeap_;
        Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> srvHeap_;
        UINT rtvIncrement_ = 0;
        UINT srvIncrement_ = 0;
        D3D12_GPU_DESCRIPTOR_HANDLE srvGpuBase_{};

        UINT bufferCount_ = 0;
        int format_ = 0;
        UINT width_ = 0;
        UINT height_ = 0;

        float brightness_ = 1.0f;
        bool pqEncode_ = false;

        std::vector<Microsoft::WRL::ComPtr<ID3D12Resource>> renderTargets_;
        std::vector<Microsoft::WRL::ComPtr<ID3D12CommandAllocator>> commandAllocators_;
        std::vector<UINT64> frameFenceValues_;
        Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> commandList_;
        Microsoft::WRL::ComPtr<ID3D12Fence> frameFence_;
        HANDLE fenceEvent_ = nullptr;
        UINT64 fenceValue_ = 0;

        std::atomic<bool> ready_{false};

        // The producer's side, opened lazily once it shows up.
        HANDLE requestMap_ = nullptr;
        SrtOverlayRequestBlock* request_ = nullptr;
        HANDLE publishMap_ = nullptr;
        const SrtOverlayPublishBlock* publish_ = nullptr;
        Microsoft::WRL::ComPtr<ID3D12Resource> sharedTextures_[SRT_OVERLAY_SURFACE_COUNT];
        Microsoft::WRL::ComPtr<ID3D12Fence> sharedFence_;
        bool surfacesOpened_ = false;

        std::atomic<long> composited_{0};
        std::atomic<long> skipped_{0};

        // Step 4 frame-cost accounting, written only on the render thread. There is no GC in the native
        // shim, and the steady composite path performs no heap allocation by construction, so the C#
        // shim's "0 bytes allocated on the render thread" holds here without a counter; only the timing
        // histogram is kept (4 us buckets up to ~2 ms).
        static constexpr int kCostBuckets = 512;
        static constexpr int kCostBucketUs = 4;
        int64_t ticksPerSecond_ = 1;
        long costHistogram_[kCostBuckets] = {};
        long costCount_ = 0;
        long costSumUs_ = 0;
        long costMaxUs_ = 0;
    };
}

#endif // SRTOVERLAY_D3D12_COMPOSITOR_H
