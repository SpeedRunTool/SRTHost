using System.Runtime.InteropServices;
using SRTOverlay.Core;

namespace SRTOverlay.DirectX12;

/// <summary>
/// Finds the Direct3D 12 vtables the shim hooks, by creating throwaway COM objects and reading them.
/// </summary>
/// <remarks>
/// <para>
/// No scanning of the game, and no waiting for the game to hand anything over. A vtable is a property
/// of the implementation rather than of any object, so a swapchain we create ourselves and throw away
/// exposes the same table the game's swapchain uses - which is why the addresses survive releasing
/// every object involved. They live in <c>dxgi.dll</c> and <c>d3d12.dll</c> data pages, which stay
/// mapped for the life of the process.
/// </para>
/// <para>
/// <b>The composition entry point is deliberate and must be kept.</b> REFramework hooks
/// <c>CreateSwapChainForHwnd</c>; creating our throwaway swapchain through
/// <c>CreateSwapChainForComposition</c> means the two tools do not trip over each other. That is
/// interoperability with the other tool this ecosystem's users already run, not an implementation
/// detail. The hwnd path is kept only as a fallback, and the C++ reference's comment about it is
/// worth preserving verbatim in spirit: it may trigger other tools that hook, but it is better than
/// failing entirely.
/// </para>
/// </remarks>
internal static unsafe class D3D12VTableResolver
{
    /// <summary>What resolution found. Addresses remain valid after the objects are released.</summary>
    internal readonly record struct VTables(
        nint SwapChainVTable,
        nint CommandQueueVTable,
        nint Present,
        nint ResizeBuffers,
        nint ExecuteCommandLists)
    {
        internal bool IsComplete => SwapChainVTable != 0 && CommandQueueVTable != 0;
    }

    /// <summary>Create throwaway objects, read their vtables, release everything.</summary>
    internal static VTables Resolve()
    {
        nint factory = 0, adapter = 0, device = 0, queue = 0, swapChain1 = 0, swapChain3 = 0;

        try
        {
            int hr = Direct3D12.CreateDXGIFactory1(Direct3D12.IID_IDXGIFactory4, out factory);
            if (hr < 0 || factory == 0)
            {
                OverlayLog.Write($"D3D12: CreateDXGIFactory1 failed, 0x{hr:X8}.");
                return default;
            }

            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)
                Direct3D12.SlotOf(Direct3D12.VTableOf(factory), Direct3D12.IDXGIFactory1_EnumAdapters1);

            // &adapter directly: a local is already fixed, so a fixed statement over one is an error.
            hr = enumAdapters1(factory, 0, &adapter);

            if (hr < 0 || adapter == 0)
            {
                OverlayLog.Write($"D3D12: EnumAdapters1 failed, 0x{hr:X8}.");
                return default;
            }

            hr = Direct3D12.D3D12CreateDevice(adapter, Direct3D12.D3D_FEATURE_LEVEL_11_0, Direct3D12.IID_ID3D12Device, out device);
            if (hr < 0 || device == 0)
            {
                OverlayLog.Write($"D3D12: D3D12CreateDevice failed, 0x{hr:X8}. This process is probably not Direct3D 12.");
                return default;
            }

            Direct3D12.D3D12CommandQueueDesc queueDesc = new()
            {
                Type = Direct3D12.D3D12_COMMAND_LIST_TYPE_DIRECT,
                Priority = 0,
                Flags = 0,
                NodeMask = 0,
            };

            var createCommandQueue = (delegate* unmanaged[Stdcall]<nint, Direct3D12.D3D12CommandQueueDesc*, Guid*, nint*, int>)
                Direct3D12.SlotOf(Direct3D12.VTableOf(device), Direct3D12.ID3D12Device_CreateCommandQueue);

            // The Guid is a static field and does need pinning; queue is a local and does not.
            fixed (Guid* iid = &Direct3D12.IID_ID3D12CommandQueue)
                hr = createCommandQueue(device, &queueDesc, iid, &queue);

            if (hr < 0 || queue == 0)
            {
                OverlayLog.Write($"D3D12: CreateCommandQueue failed, 0x{hr:X8}.");
                return default;
            }

            nint queueVTable = Direct3D12.VTableOf(queue);
            nint executeCommandLists = Direct3D12.SlotOf(queueVTable, Direct3D12.ID3D12CommandQueue_ExecuteCommandLists);

            swapChain1 = CreateThrowawaySwapChain(factory, queue);
            if (swapChain1 == 0)
                return default;

            hr = Direct3D12.QueryInterface(swapChain1, Direct3D12.IID_IDXGISwapChain3, out swapChain3);
            if (hr < 0 || swapChain3 == 0)
            {
                OverlayLog.Write($"D3D12: QueryInterface for IDXGISwapChain3 failed, 0x{hr:X8}.");
                return default;
            }

            nint swapVTable = Direct3D12.VTableOf(swapChain3);

            return new VTables(
                SwapChainVTable: swapVTable,
                CommandQueueVTable: queueVTable,
                Present: Direct3D12.SlotOf(swapVTable, Direct3D12.IDXGISwapChain_Present),
                ResizeBuffers: Direct3D12.SlotOf(swapVTable, Direct3D12.IDXGISwapChain_ResizeBuffers),
                ExecuteCommandLists: executeCommandLists);
        }
        catch (Exception exception)
        {
            OverlayLog.WriteException("D3D12 vtable resolution", exception);
            return default;
        }
        finally
        {
            // Reverse creation order. The vtable addresses read above outlive all of this, because
            // they point into the loaded modules rather than into any object.
            Direct3D12.Release(swapChain3);
            Direct3D12.Release(swapChain1);
            Direct3D12.Release(queue);
            Direct3D12.Release(device);
            Direct3D12.Release(adapter);
            Direct3D12.Release(factory);
        }
    }

    /// <summary>A 1x1 swapchain, by composition if possible and by hwnd if not.</summary>
    private static nint CreateThrowawaySwapChain(nint factory, nint queue)
    {
        Direct3D12.DxgiSwapChainDesc1 desc = new()
        {
            Width = 1,
            Height = 1,
            Format = Direct3D12.DXGI_FORMAT_B8G8R8A8_UNORM,
            Stereo = 0,
            SampleDesc = new Direct3D12.DxgiSampleDesc { Count = 1, Quality = 0 },
            BufferUsage = Direct3D12.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = 2,
            Scaling = Direct3D12.DXGI_SCALING_STRETCH,
            SwapEffect = Direct3D12.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            AlphaMode = Direct3D12.DXGI_ALPHA_MODE_PREMULTIPLIED,
            Flags = 0,
        };

        nint swapChain = 0;
        var createForComposition = (delegate* unmanaged[Stdcall]<nint, nint, Direct3D12.DxgiSwapChainDesc1*, nint, nint*, int>)
            Direct3D12.SlotOf(Direct3D12.VTableOf(factory), Direct3D12.IDXGIFactory2_CreateSwapChainForComposition);

        int hr = createForComposition(factory, queue, &desc, 0, &swapChain);
        if (hr >= 0 && swapChain != 0)
            return swapChain;

        OverlayLog.Write(
            $"D3D12: CreateSwapChainForComposition failed, 0x{hr:X8}; falling back to " +
            "CreateSwapChainForHwnd with a throwaway window. This may be noticed by other tools that " +
            "hook that entry point, but it is better than failing entirely.");

        return CreateThrowawaySwapChainForHwnd(factory, queue, desc);
    }

    /// <summary>The fallback: a real window, used once and destroyed.</summary>
    private static nint CreateThrowawaySwapChainForHwnd(nint factory, nint queue, Direct3D12.DxgiSwapChainDesc1 desc)
    {
        // Composition swapchains accept premultiplied alpha and a 1x1 size; hwnd ones are fussier,
        // so the descriptor is adjusted to what that path actually accepts rather than reused as-is.
        desc.Format = Direct3D12.DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SwapEffect = Direct3D12.DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode = Direct3D12.DXGI_ALPHA_MODE_UNSPECIFIED;
        desc.Width = 2;
        desc.Height = 2;

        nint module = Win32.GetModuleHandleW(null);
        nint windowProc = Win32.GetProcAddress(Win32.GetModuleHandleW("user32.dll"), "DefWindowProcW");
        if (windowProc == 0)
            return 0;

        fixed (char* className = "SRTOverlayDummy")
        {
            Win32.WndClassExW windowClass = new()
            {
                cbSize = (uint)sizeof(Win32.WndClassExW),
                lpfnWndProc = windowProc,
                hInstance = module,
                lpszClassName = className,
            };

            ushort atom = Win32.RegisterClassExW(&windowClass);
            if (atom == 0)
            {
                OverlayLog.Write($"D3D12: RegisterClassExW failed, {Marshal.GetLastWin32Error()}.");
                return 0;
            }

            nint window = Win32.CreateWindowExW(0, className, className, 0, 0, 0, 2, 2, 0, 0, module, 0);
            if (window == 0)
            {
                OverlayLog.Write($"D3D12: CreateWindowExW failed, {Marshal.GetLastWin32Error()}.");
                Win32.UnregisterClassW(className, module);
                return 0;
            }

            nint swapChain = 0;
            try
            {
                var createForHwnd = (delegate* unmanaged[Stdcall]<nint, nint, nint, Direct3D12.DxgiSwapChainDesc1*, nint, nint, nint*, int>)
                    Direct3D12.SlotOf(Direct3D12.VTableOf(factory), Direct3D12.IDXGIFactory2_CreateSwapChainForHwnd);

                int hr = createForHwnd(factory, queue, window, &desc, 0, 0, &swapChain);
                if (hr < 0 || swapChain == 0)
                {
                    OverlayLog.Write($"D3D12: CreateSwapChainForHwnd fallback failed, 0x{hr:X8}.");
                    return 0;
                }

                return swapChain;
            }
            finally
            {
                Win32.DestroyWindow(window);
                Win32.UnregisterClassW(className, module);
            }
        }
    }
}

/// <summary>The window Win32 the hwnd fallback needs, and nothing more.</summary>
internal static unsafe partial class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WndClassExW
    {
        internal uint cbSize;
        internal uint style;
        internal nint lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal nint hInstance;
        internal nint hIcon;
        internal nint hCursor;
        internal nint hbrBackground;
        internal char* lpszMenuName;
        internal char* lpszClassName;
        internal nint hIconSm;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial nint GetProcAddress(nint module, string procName);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(WndClassExW* windowClass);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CreateWindowExW(
        uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterClassW(char* className, nint instance);

    /// <summary>Wait forever, for the fence-completion event on the composite path's allocator reuse.</summary>
    internal const uint INFINITE = 0xFFFFFFFF;

    /// <summary>An auto-reset, initially-unsignalled event for waiting on a fence completion.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateEventW(nint attributes, int manualReset, int initialState, char* name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
