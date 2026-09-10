using System.Runtime.InteropServices;

namespace SRTOverlay.DirectX12;

/// <summary>
/// The Direct3D 12 and DXGI surface the shim uses, as raw COM.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately minimal: four exported functions, a few structures, and vtable slots called by
/// index. There is no object model here because the shim does not want one - what it needs is the
/// address of a vtable, which is the one thing a typed wrapper exists to hide.
/// </para>
/// <para>
/// <b>The slot indices are properties of the COM interfaces, not of any language or compiler</b>, so
/// they carry over unchanged from the C++ reference and are stable for the life of the interfaces.
/// Each is spelled out with the inheritance chain that produces it, because an off-by-one here calls
/// the wrong function through a pointer and crashes a game rather than failing a build.
/// </para>
/// </remarks>
internal static unsafe partial class Direct3D12
{
    // ---- IUnknown: QueryInterface(0) AddRef(1) Release(2) ----
    internal const int IUnknown_QueryInterface = 0;
    internal const int IUnknown_Release = 2;

    // ---- IDXGIFactory1 ----
    // IUnknown(0-2), IDXGIObject(3-6: SetPrivateData, SetPrivateDataInterface, GetPrivateData,
    // GetParent), IDXGIFactory(7-11: EnumAdapters, MakeWindowAssociation, GetWindowAssociation,
    // CreateSwapChain, CreateSoftwareAdapter), then IDXGIFactory1 adds EnumAdapters1 at 12.
    internal const int IDXGIFactory1_EnumAdapters1 = 12;

    // IDXGIFactory2 continues: IsWindowedStereoEnabled(14), CreateSwapChainForHwnd(15),
    // CreateSwapChainForCoreWindow(16), GetSharedResourceAdapterLuid(17),
    // RegisterStereoStatusWindow(18), RegisterStereoStatusEvent(19), UnregisterStereoStatus(20),
    // RegisterOcclusionStatusWindow(21), RegisterOcclusionStatusEvent(22),
    // UnregisterOcclusionStatus(23), CreateSwapChainForComposition(24).
    internal const int IDXGIFactory2_CreateSwapChainForHwnd = 15;
    internal const int IDXGIFactory2_CreateSwapChainForComposition = 24;

    // ---- ID3D12Device ----
    // IUnknown(0-2), ID3D12Object(3-6: GetPrivateData, SetPrivateData, SetPrivateDataInterface,
    // SetName), then GetNodeCount(7), CreateCommandQueue(8).
    internal const int ID3D12Device_CreateCommandQueue = 8;

    // ---- ID3D12CommandQueue ----
    // IUnknown(0-2), ID3D12Object(3-6), ID3D12DeviceChild GetDevice(7), then
    // UpdateTileMappings(8), CopyTileMappings(9), ExecuteCommandLists(10).
    internal const int ID3D12CommandQueue_ExecuteCommandLists = 10;

    // ---- IDXGISwapChain ----
    // IUnknown(0-2), IDXGIObject(3-6), IDXGIDeviceSubObject GetDevice(7), then Present(8),
    // GetBuffer(9), SetFullscreenState(10), GetFullscreenState(11), GetDesc(12), ResizeBuffers(13).
    internal const int IDXGISwapChain_Present = 8;
    internal const int IDXGISwapChain_ResizeBuffers = 13;

    internal static readonly Guid IID_IDXGIFactory4 = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
    internal static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    internal static readonly Guid IID_ID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    internal static readonly Guid IID_ID3D12CommandQueue = new("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
    internal static readonly Guid IID_IDXGISwapChain1 = new("790a45f7-0d42-4876-983a-0a55cfe6f4aa");
    internal static readonly Guid IID_IDXGISwapChain3 = new("94d99bdb-f1f8-4ab0-b236-7da0170edab1");

    internal const int D3D_FEATURE_LEVEL_11_0 = 0xB000;
    internal const int D3D12_COMMAND_LIST_TYPE_DIRECT = 0;

    internal const int DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    internal const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    internal const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;
    internal const int DXGI_SCALING_STRETCH = 0;
    internal const int DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL = 3;
    internal const int DXGI_SWAP_EFFECT_FLIP_DISCARD = 4;
    internal const int DXGI_ALPHA_MODE_UNSPECIFIED = 0;
    internal const int DXGI_ALPHA_MODE_PREMULTIPLIED = 1;

    [LibraryImport("dxgi.dll")]
    internal static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [LibraryImport("d3d12.dll")]
    internal static partial int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, in Guid riid, out nint device);

    /// <summary>The vtable pointer of a COM object, which is its first machine word.</summary>
    internal static nint VTableOf(nint comObject) => *(nint*)comObject;

    /// <summary>One entry of a vtable, by index.</summary>
    internal static nint SlotOf(nint vtable, int slot) => ((nint*)vtable)[slot];

    /// <summary><c>IUnknown::Release</c>, called through the object's own vtable.</summary>
    internal static uint Release(nint comObject)
    {
        if (comObject == 0)
            return 0;

        var release = (delegate* unmanaged[Stdcall]<nint, uint>)SlotOf(VTableOf(comObject), IUnknown_Release);
        return release(comObject);
    }

    /// <summary><c>IUnknown::QueryInterface</c>, called through the object's own vtable.</summary>
    internal static int QueryInterface(nint comObject, in Guid riid, out nint result)
    {
        result = 0;
        if (comObject == 0)
            return unchecked((int)0x80004003); // E_POINTER

        fixed (Guid* iid = &riid)
        fixed (nint* output = &result)
        {
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)
                SlotOf(VTableOf(comObject), IUnknown_QueryInterface);
            return queryInterface(comObject, iid, output);
        }
    }

    /// <summary><c>D3D12_COMMAND_QUEUE_DESC</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D12CommandQueueDesc
    {
        internal int Type;
        internal int Priority;
        internal uint Flags;
        internal uint NodeMask;
    }

    /// <summary><c>DXGI_SAMPLE_DESC</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiSampleDesc
    {
        internal uint Count;
        internal uint Quality;
    }

    /// <summary><c>DXGI_SWAP_CHAIN_DESC1</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiSwapChainDesc1
    {
        internal uint Width;
        internal uint Height;
        internal int Format;
        internal int Stereo;
        internal DxgiSampleDesc SampleDesc;
        internal uint BufferUsage;
        internal uint BufferCount;
        internal int Scaling;
        internal int SwapEffect;
        internal int AlphaMode;
        internal uint Flags;
    }
}
