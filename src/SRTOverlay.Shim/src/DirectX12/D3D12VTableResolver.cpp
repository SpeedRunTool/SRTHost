#include "D3D12VTableResolver.h"

#include <windows.h>

#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include "OverlayLog.h"

using Microsoft::WRL::ComPtr;
using srtoverlay::core::OverlayLog;

namespace srtoverlay::directx12
{
    namespace
    {
        // The vtable array of a COM object is its first machine word.
        void** VTableOf(void* comObject)
        {
            return *reinterpret_cast<void***>(comObject);
        }

        // A 1x1 swapchain, by composition if possible and by hwnd if not. Composition first, so the
        // two tools that hook DXGI do not contend for the hwnd entry point.
        ComPtr<IDXGISwapChain1> CreateThrowawaySwapChain(IDXGIFactory2* factory, ID3D12CommandQueue* queue)
        {
            DXGI_SWAP_CHAIN_DESC1 desc{};
            desc.Width = 1;
            desc.Height = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 2;
            desc.Scaling = DXGI_SCALING_STRETCH;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;

            ComPtr<IDXGISwapChain1> swapChain;
            HRESULT hr = factory->CreateSwapChainForComposition(queue, &desc, nullptr, &swapChain);
            if (SUCCEEDED(hr) && swapChain)
                return swapChain;

            OverlayLog::WriteHResult("D3D12: CreateSwapChainForComposition; falling back to hwnd", hr);

            // Fallback: a real message-only-ish window, used once and destroyed. Composition swapchains
            // accept premultiplied alpha and 1x1; hwnd ones are fussier, so the descriptor is adjusted.
            desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;
            desc.Width = 2;
            desc.Height = 2;

            HMODULE module = GetModuleHandleW(nullptr);
            const wchar_t* className = L"SRTOverlayDummy";

            WNDCLASSEXW wc{};
            wc.cbSize = sizeof(wc);
            wc.lpfnWndProc = DefWindowProcW;
            wc.hInstance = module;
            wc.lpszClassName = className;

            ATOM atom = RegisterClassExW(&wc);
            if (atom == 0)
            {
                OverlayLog::Write("D3D12: RegisterClassExW failed for the hwnd fallback.");
                return nullptr;
            }

            HWND window = CreateWindowExW(0, className, className, 0, 0, 0, 2, 2, nullptr, nullptr, module, nullptr);
            if (window == nullptr)
            {
                OverlayLog::Write("D3D12: CreateWindowExW failed for the hwnd fallback.");
                UnregisterClassW(className, module);
                return nullptr;
            }

            hr = factory->CreateSwapChainForHwnd(queue, window, &desc, nullptr, nullptr, &swapChain);
            DestroyWindow(window);
            UnregisterClassW(className, module);

            if (FAILED(hr) || !swapChain)
            {
                OverlayLog::WriteHResult("D3D12: CreateSwapChainForHwnd fallback", hr);
                return nullptr;
            }

            return swapChain;
        }
    }

    ResolvedVTables ResolveVTables()
    {
        ResolvedVTables result;

        ComPtr<IDXGIFactory4> factory;
        HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (FAILED(hr) || !factory)
        {
            OverlayLog::WriteHResult("D3D12: CreateDXGIFactory1", hr);
            return result;
        }

        ComPtr<IDXGIAdapter1> adapter;
        hr = factory->EnumAdapters1(0, &adapter);
        if (FAILED(hr) || !adapter)
        {
            OverlayLog::WriteHResult("D3D12: EnumAdapters1", hr);
            return result;
        }

        ComPtr<ID3D12Device> device;
        hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
        if (FAILED(hr) || !device)
        {
            OverlayLog::WriteHResult("D3D12: D3D12CreateDevice (this process is probably not Direct3D 12)", hr);
            return result;
        }

        D3D12_COMMAND_QUEUE_DESC queueDesc{};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;

        ComPtr<ID3D12CommandQueue> queue;
        hr = device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue));
        if (FAILED(hr) || !queue)
        {
            OverlayLog::WriteHResult("D3D12: CreateCommandQueue", hr);
            return result;
        }

        ComPtr<IDXGISwapChain1> swapChain1 = CreateThrowawaySwapChain(factory.Get(), queue.Get());
        if (!swapChain1)
            return result;

        ComPtr<IDXGISwapChain3> swapChain3;
        hr = swapChain1.As(&swapChain3);
        if (FAILED(hr) || !swapChain3)
        {
            OverlayLog::WriteHResult("D3D12: QueryInterface for IDXGISwapChain3", hr);
            return result;
        }

        // The vtable arrays live in the loaded modules, so they outlive every object released below.
        result.swapChainVTable = VTableOf(swapChain3.Get());
        result.commandQueueVTable = VTableOf(queue.Get());
        return result;
    }
}
