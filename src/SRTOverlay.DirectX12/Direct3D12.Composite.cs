using System.Runtime.InteropServices;

namespace SRTOverlay.DirectX12;

/// <summary>
/// The rest of the Direct3D 12 surface the compositor and the throwaway producer need: enough to open
/// a shared texture and fence, build a one-quad pipeline, and submit it on the game's queue.
/// </summary>
/// <remarks>
/// <para>
/// Split from <see cref="Direct3D12"/>'s vtable-resolution half only to keep each file readable; it is
/// the same class and the same hand-rolled interop, and the reasoning in the other file's header -
/// no object model, raw vtable pointers, no Vortice inside the injected binary - applies here too.
/// </para>
/// <para>
/// <b>The slot indices below are the true COM vtable indices, which are not the same as the ordinal of
/// a method in the C header.</b> A COM method that returns a structure by value (<c>GetAdapterLuid</c>,
/// <c>GetCPUDescriptorHandleForHeapStart</c>, <c>GetResourceAllocationInfo</c>, …) is emitted as
/// <i>two</i> declarations in the MIDL-generated C header - one returning the value, one taking a
/// hidden out-parameter - but it occupies exactly <i>one</i> vtable slot. Every index here that sits
/// after such a method is therefore offset from the header ordinal, and each is spelled out with the
/// collapses that produce it, because an off-by-one calls the wrong function through a pointer and
/// crashes the game rather than failing a build. The by-value returns are handled on x64's convention:
/// a structure of eight bytes or fewer comes back in <c>RAX</c>, so those methods are called through a
/// delegate whose return type is the eight-byte value itself. See the note on each.
/// </para>
/// </remarks>
internal static unsafe partial class Direct3D12
{
    // ---- ID3D12Device, correcting for the three by-value returns (GetResourceAllocationInfo header
    //      25/26, GetCustomHeapProperties 27/28, GetAdapterLuid 45/46). Methods before slot 25 are
    //      unaffected; CreateCommittedResource onward loses two, GetAdapterLuid loses two. ----
    internal const int ID3D12Device_CreateCommandAllocator = 9;
    internal const int ID3D12Device_CreateGraphicsPipelineState = 10;
    internal const int ID3D12Device_CreateCommandList = 12;
    internal const int ID3D12Device_CreateDescriptorHeap = 14;
    internal const int ID3D12Device_GetDescriptorHandleIncrementSize = 15;
    internal const int ID3D12Device_CreateRootSignature = 16;
    internal const int ID3D12Device_CreateShaderResourceView = 18;
    internal const int ID3D12Device_CreateRenderTargetView = 20;
    internal const int ID3D12Device_CreateCommittedResource = 27;   // header 29 − 2
    internal const int ID3D12Device_CreateSharedHandle = 31;        // header 33 − 2
    internal const int ID3D12Device_OpenSharedHandle = 32;          // header 34 − 2
    internal const int ID3D12Device_OpenSharedHandleByName = 33;    // header 35 − 2
    internal const int ID3D12Device_CreateFence = 36;               // header 38 − 2
    internal const int ID3D12Device_GetAdapterLuid = 43;            // header 45 − 2, returns LUID by value

    // ---- IDXGIFactory4 ----
    internal const int IDXGIFactory4_EnumAdapterByLuid = 26;

    // ---- ID3D12DescriptorHeap. THREE by-value returns are doubled in the C header and each collapses
    //      to one slot: GetDesc (header 8/9), GetCPUDescriptorHandleForHeapStart (10/11) and
    //      GetGPUDescriptorHandleForHeapStart (12/13). Real slots: GetDesc 8, GetCPU 9, GetGPU 10.
    //      Missing the GetDesc collapse put both handles one slot too high - GetCPU read GetGPU and
    //      GetGPU read past the vtable - which OverlaySelfCheck caught as a zero GPU handle. ----
    internal const int ID3D12DescriptorHeap_GetCPUDescriptorHandleForHeapStart = 9;  // header 10 − 1 (GetDesc)
    internal const int ID3D12DescriptorHeap_GetGPUDescriptorHandleForHeapStart = 10; // header 12 − 2 (GetDesc, GetCPU)

    // ---- ID3D12GraphicsCommandList: no by-value returns before any of these. ----
    internal const int ID3D12GraphicsCommandList_Close = 9;
    internal const int ID3D12GraphicsCommandList_Reset = 10;
    internal const int ID3D12GraphicsCommandList_DrawInstanced = 12;
    internal const int ID3D12GraphicsCommandList_IASetPrimitiveTopology = 20;
    internal const int ID3D12GraphicsCommandList_RSSetViewports = 21;
    internal const int ID3D12GraphicsCommandList_RSSetScissorRects = 22;
    internal const int ID3D12GraphicsCommandList_SetPipelineState = 25;
    internal const int ID3D12GraphicsCommandList_ResourceBarrier = 26;
    internal const int ID3D12GraphicsCommandList_SetDescriptorHeaps = 28;
    internal const int ID3D12GraphicsCommandList_SetGraphicsRootSignature = 30;
    internal const int ID3D12GraphicsCommandList_SetGraphicsRootDescriptorTable = 32;
    internal const int ID3D12GraphicsCommandList_SetGraphicsRoot32BitConstants = 36;
    internal const int ID3D12GraphicsCommandList_ClearRenderTargetView = 48;
    internal const int ID3D12GraphicsCommandList_OMSetRenderTargets = 46;

    // ---- ID3D12CommandAllocator ----
    internal const int ID3D12CommandAllocator_Reset = 8;

    // ---- ID3D12CommandQueue (ExecuteCommandLists already in the other file, slot 10) ----
    internal const int ID3D12CommandQueue_Signal = 14;
    internal const int ID3D12CommandQueue_Wait = 15;
    internal const int ID3D12CommandQueue_GetDesc = 18; // returns D3D12_COMMAND_QUEUE_DESC by value

    // ---- ID3D12Fence ----
    internal const int ID3D12Fence_GetCompletedValue = 8;   // returns UINT64 (scalar, one slot always)
    internal const int ID3D12Fence_SetEventOnCompletion = 9;
    internal const int ID3D12Fence_Signal = 10;

    // ---- IDXGISwapChain(3) ----
    internal const int IDXGISwapChain_GetDevice = 7;
    internal const int IDXGISwapChain_GetBuffer = 9;
    internal const int IDXGISwapChain_GetDesc = 12;
    internal const int IDXGISwapChain3_GetCurrentBackBufferIndex = 36;

    // ---- ID3DBlob (ID3D10Blob) ----
    internal const int ID3DBlob_GetBufferPointer = 3;   // returns void* by value
    internal const int ID3DBlob_GetBufferSize = 4;      // returns SIZE_T by value

    internal static readonly Guid IID_ID3D12Resource = new("696442be-a72e-4059-bc79-5b5c98040fad");
    internal static readonly Guid IID_ID3D12Fence = new("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
    internal static readonly Guid IID_ID3D12GraphicsCommandList = new("5b160d0f-ac1b-4185-8ba8-b3ae42a5a455");
    internal static readonly Guid IID_ID3D12RootSignature = new("c54a6b66-72df-4ee8-8be5-a946a1429214");
    internal static readonly Guid IID_ID3D12PipelineState = new("765a30f3-f624-4c6f-a828-ace948622445");
    internal static readonly Guid IID_ID3D12DescriptorHeap = new("8efb471d-616c-4f49-90f7-127bb763fa51");
    internal static readonly Guid IID_ID3D12CommandAllocator = new("6102dee4-af59-4b09-b999-b44d73f09b24");

    internal const int D3D12_COMMAND_QUEUE_FLAG_NONE = 0;
    internal const int D3D12_FENCE_FLAG_NONE = 0;
    internal const int D3D12_FENCE_FLAG_SHARED = 0x1;
    internal const int D3D12_HEAP_FLAG_SHARED = 0x1;
    internal const int D3D12_HEAP_FLAG_NONE = 0;
    internal const int D3D12_COMMAND_LIST_TYPE_COPY = 3;
    internal const int D3D12_HEAP_TYPE_DEFAULT = 1;
    internal const int D3D12_RESOURCE_DIMENSION_TEXTURE2D = 3;
    internal const int D3D12_TEXTURE_LAYOUT_UNKNOWN = 0;
    internal const int D3D12_RESOURCE_FLAG_NONE = 0;
    internal const int D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET = 0x1;
    internal const int D3D12_RESOURCE_STATE_COMMON = 0;
    internal const int D3D12_RESOURCE_STATE_RENDER_TARGET = 0x4;
    internal const int D3D12_RESOURCE_STATE_PRESENT = 0;
    internal const int D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE = 0x80;
    internal const int D3D12_RESOURCE_BARRIER_TYPE_TRANSITION = 0;
    internal const int D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES = -1; // 0xFFFFFFFF as UINT
    internal const int D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV = 0;
    internal const int D3D12_DESCRIPTOR_HEAP_TYPE_RTV = 2;
    internal const int D3D12_DESCRIPTOR_HEAP_FLAG_NONE = 0;
    internal const int D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE = 0x1;
    internal const int D3D12_DESCRIPTOR_RANGE_TYPE_SRV = 0;
    internal const int D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE = 0;
    internal const int D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS = 1;
    internal const int D3D12_SHADER_VISIBILITY_ALL = 0;
    internal const int D3D12_SHADER_VISIBILITY_VERTEX = 1;
    internal const int D3D12_SHADER_VISIBILITY_PIXEL = 5;
    internal const int D3D12_ROOT_SIGNATURE_FLAG_NONE = 0;
    internal const int D3D12_FILTER_MIN_MAG_MIP_POINT = 0;
    internal const int D3D12_TEXTURE_ADDRESS_MODE_CLAMP = 3;
    internal const int D3D12_COMPARISON_FUNC_ALWAYS = 8;
    internal const int D3D12_STATIC_BORDER_COLOR_TRANSPARENT_BLACK = 0;
    internal const int D3D_ROOT_SIGNATURE_VERSION_1_0 = 0x1;

    internal const int D3D12_FILL_MODE_SOLID = 3;
    internal const int D3D12_CULL_MODE_NONE = 1;
    internal const int D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF = 0;
    internal const int D3D12_DEPTH_WRITE_MASK_ZERO = 0;
    internal const int D3D12_COMPARISON_FUNC_LESS = 2;
    internal const int D3D12_STENCIL_OP_KEEP = 1;
    internal const int D3D12_BLEND_ONE = 2;
    internal const int D3D12_BLEND_INV_SRC_ALPHA = 6;
    internal const int D3D12_BLEND_OP_ADD = 1;
    internal const int D3D12_LOGIC_OP_NOOP = 0;
    internal const byte D3D12_COLOR_WRITE_ENABLE_ALL = 0x0F;
    internal const int D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE = 3;
    internal const int D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP = 5;
    internal const uint D3D12_DEFAULT_SAMPLE_MASK = 0xFFFFFFFF;
    internal const int D3D12_INDEX_BUFFER_STRIP_CUT_VALUE_DISABLED = 0;
    internal const int D3D12_PIPELINE_STATE_FLAG_NONE = 0;

    internal const uint DXGI_PRESENT_TEST = 0x00000001;

    // Back-buffer formats the composite adapts to. R8G8B8A8_UNORM (28) and B8G8R8A8_UNORM (87) are in
    // the other partial; these are the sRGB and HDR variants a game may present in.
    internal const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    internal const int DXGI_FORMAT_R10G10B10A2_UNORM = 24;
    internal const int DXGI_FORMAT_R8G8B8A8_UNORM_SRGB = 29;
    internal const int DXGI_FORMAT_B8G8R8A8_UNORM_SRGB = 91;

    [LibraryImport("d3d12.dll")]
    internal static partial int D3D12SerializeRootSignature(
        RootSignatureDesc* desc, int version, out nint blob, out nint errorBlob);

    // ==== By-value-returning COM methods, called on x64's RAX-return convention ====

    // These three COM methods return an eight-byte structure by value. Although the x64 ABI would put
    // an eight-byte POD in RAX, the shipped d3d12.dll returns all of them by hidden pointer (sret) -
    // measured, not assumed: OverlaySelfCheck's discriminator showed the RAX form returning the return
    // pointer itself rather than the value. So each is called member-sret style: this first, a pointer
    // to the caller's buffer second, and the value read back from the buffer. Calling them as RAX
    // returns yields a stack address, which is what crashed CreateRenderTargetView the first time.

    /// <summary>The adapter LUID of a device, packed low into the low 32 bits and high into the high.</summary>
    internal static long GetAdapterLuid(nint device)
    {
        long luid = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, long*, void>)SlotOf(VTableOf(device), ID3D12Device_GetAdapterLuid);
        fn(device, &luid);
        return luid;
    }

    /// <summary>The CPU descriptor handle at the start of a heap.</summary>
    internal static nuint GetCpuDescriptorHandleForHeapStart(nint heap)
    {
        nuint handle = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, nuint*, void>)SlotOf(VTableOf(heap), ID3D12DescriptorHeap_GetCPUDescriptorHandleForHeapStart);
        fn(heap, &handle);
        return handle;
    }

    /// <summary>The GPU descriptor handle at the start of a heap.</summary>
    internal static ulong GetGpuDescriptorHandleForHeapStart(nint heap)
    {
        ulong handle = 0;
        var fn = (delegate* unmanaged[Stdcall]<nint, ulong*, void>)SlotOf(VTableOf(heap), ID3D12DescriptorHeap_GetGPUDescriptorHandleForHeapStart);
        fn(heap, &handle);
        return handle;
    }

    /// <summary>
    /// The CPU-handle slot called the RAX way (value in the return register), for the self-check's ABI
    /// discriminator only. The real getter above uses sret; this exists to prove that is the right one.
    /// </summary>
    internal static nuint GetCpuDescriptorHandleForHeapStartRax(nint heap)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nuint>)SlotOf(VTableOf(heap), ID3D12DescriptorHeap_GetCPUDescriptorHandleForHeapStart);
        return fn(heap);
    }

    internal static uint GetDescriptorHandleIncrementSize(nint device, int heapType)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int, uint>)SlotOf(VTableOf(device), ID3D12Device_GetDescriptorHandleIncrementSize);
        return fn(device, heapType);
    }

    internal static int Present(nint swapChain, uint syncInterval, uint flags)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)SlotOf(VTableOf(swapChain), IDXGISwapChain_Present);
        return fn(swapChain, syncInterval, flags);
    }

    internal static uint GetCurrentBackBufferIndex(nint swapChain)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint>)SlotOf(VTableOf(swapChain), IDXGISwapChain3_GetCurrentBackBufferIndex);
        return fn(swapChain);
    }

    /// <summary>The <c>D3D12_COMMAND_LIST_TYPE</c> of a command queue - <c>DIRECT</c> is what presents.</summary>
    /// <remarks>
    /// <para>
    /// <c>ID3D12CommandQueue::GetDesc</c> returns a 16-byte <c>D3D12_COMMAND_QUEUE_DESC</c> by value,
    /// so on x64 it uses the hidden-return-pointer (sret) convention. <b>For a member function the
    /// order is <c>this</c> first, the return pointer second</b> - MSVC prepends the sret pointer after
    /// <c>this</c>, not before it - so the slot is called as <c>void(this, retptr)</c>. Getting this
    /// backwards writes the descriptor into the queue object's own memory and corrupts it, which is a
    /// crash that surfaces far from here; <c>OverlaySelfCheck</c> pins the correct order against real
    /// queues in a throwaway process.
    /// </para>
    /// <para>
    /// The x86 shim's convention differs and is verified when the 32-bit backend is exercised; the
    /// step-3 gate runs x64.
    /// </para>
    /// </remarks>
    internal static int GetCommandQueueType(nint queue)
    {
        D3D12CommandQueueDesc desc = default;
        var fn = (delegate* unmanaged[Stdcall]<nint, D3D12CommandQueueDesc*, void>)SlotOf(VTableOf(queue), ID3D12CommandQueue_GetDesc);
        fn(queue, &desc);
        return desc.Type;
    }

    internal static ulong GetFenceCompletedValue(nint fence)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, ulong>)SlotOf(VTableOf(fence), ID3D12Fence_GetCompletedValue);
        return fn(fence);
    }

    // ==== Ordinary vtable calls returning HRESULT / void ====

    internal static int GetDeviceOf(nint swapChain, in Guid riid, out nint device)
    {
        device = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &device)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)SlotOf(VTableOf(swapChain), IDXGISwapChain_GetDevice);
            return fn(swapChain, iid, output);
        }
    }

    internal static int GetSwapChainDesc(nint swapChain, out DxgiSwapChainDesc desc)
    {
        fixed (DxgiSwapChainDesc* d = &desc)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, DxgiSwapChainDesc*, int>)SlotOf(VTableOf(swapChain), IDXGISwapChain_GetDesc);
            return fn(swapChain, d);
        }
    }

    internal static int GetBuffer(nint swapChain, uint index, in Guid riid, out nint surface)
    {
        surface = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &surface)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)SlotOf(VTableOf(swapChain), IDXGISwapChain_GetBuffer);
            return fn(swapChain, index, iid, output);
        }
    }

    internal static int CreateDescriptorHeap(nint device, in DescriptorHeapDesc desc, in Guid riid, out nint heap)
    {
        heap = 0;
        fixed (DescriptorHeapDesc* d = &desc)
        fixed (Guid* iid = &riid)
        fixed (nint* output = &heap)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, DescriptorHeapDesc*, Guid*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_CreateDescriptorHeap);
            return fn(device, d, iid, output);
        }
    }

    internal static void CreateRenderTargetView(nint device, nint resource, nuint cpuHandle)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, nuint, void>)SlotOf(VTableOf(device), ID3D12Device_CreateRenderTargetView);
        fn(device, resource, 0, cpuHandle); // null pDesc: use the resource's own typed format
    }

    internal static void CreateShaderResourceView(nint device, nint resource, nuint cpuHandle)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, nuint, void>)SlotOf(VTableOf(device), ID3D12Device_CreateShaderResourceView);
        fn(device, resource, 0, cpuHandle); // null pDesc: default 2D view of the resource's typed format
    }

    internal static int CreateCommandQueue(nint device, in D3D12CommandQueueDesc desc, in Guid riid, out nint queue)
    {
        queue = 0;
        fixed (D3D12CommandQueueDesc* d = &desc)
        fixed (Guid* iid = &riid)
        fixed (nint* output = &queue)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, D3D12CommandQueueDesc*, Guid*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_CreateCommandQueue);
            return fn(device, d, iid, output);
        }
    }

    internal static int CreateCommandAllocator(nint device, int type, in Guid riid, out nint allocator)
    {
        allocator = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &allocator)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_CreateCommandAllocator);
            return fn(device, type, iid, output);
        }
    }

    internal static int CreateCommandList(nint device, uint nodeMask, int type, nint allocator, nint initialState, in Guid riid, out nint list)
    {
        list = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &list)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, int, nint, nint, Guid*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_CreateCommandList);
            return fn(device, nodeMask, type, allocator, initialState, iid, output);
        }
    }

    internal static int CreateFence(nint device, ulong initialValue, int flags, in Guid riid, out nint fence)
    {
        fence = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &fence)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, ulong, int, Guid*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_CreateFence);
            return fn(device, initialValue, flags, iid, output);
        }
    }

    internal static int CreateRootSignature(nint device, nint blobPointer, nuint blobLength, in Guid riid, out nint rootSignature)
    {
        rootSignature = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &rootSignature)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, nint, nuint, Guid*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_CreateRootSignature);
            return fn(device, 0, blobPointer, blobLength, iid, output);
        }
    }

    internal static int CreateGraphicsPipelineState(nint device, GraphicsPipelineStateDesc* desc, in Guid riid, out nint pipelineState)
    {
        pipelineState = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &pipelineState)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, GraphicsPipelineStateDesc*, Guid*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_CreateGraphicsPipelineState);
            return fn(device, desc, iid, output);
        }
    }

    internal static int CreateCommittedResource(
        nint device, in HeapProperties heapProperties, int heapFlags, in ResourceDesc desc,
        int initialState, in Guid riid, out nint resource)
    {
        resource = 0;
        fixed (HeapProperties* hp = &heapProperties)
        fixed (ResourceDesc* rd = &desc)
        fixed (Guid* iid = &riid)
        fixed (nint* output = &resource)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, HeapProperties*, int, ResourceDesc*, int, nint, Guid*, nint*, int>)
                SlotOf(VTableOf(device), ID3D12Device_CreateCommittedResource);
            return fn(device, hp, heapFlags, rd, initialState, 0, iid, output);
        }
    }

    internal static int CreateSharedHandle(nint device, nint obj, string name, out nint handle)
    {
        handle = 0;
        fixed (char* namePtr = name)
        fixed (nint* output = &handle)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, char*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_CreateSharedHandle);
            return fn(device, obj, 0, OverlaySharedSurfaceAccess, namePtr, output);
        }
    }

    internal static int OpenSharedHandleByName(nint device, string name, out nint ntHandle)
    {
        ntHandle = 0;
        fixed (char* namePtr = name)
        fixed (nint* output = &ntHandle)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, char*, uint, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_OpenSharedHandleByName);
            return fn(device, namePtr, OverlaySharedSurfaceAccess, output);
        }
    }

    internal static int OpenSharedHandle(nint device, nint ntHandle, in Guid riid, out nint obj)
    {
        obj = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &obj)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)SlotOf(VTableOf(device), ID3D12Device_OpenSharedHandle);
            return fn(device, ntHandle, iid, output);
        }
    }

    internal static int CreateSwapChainForHwnd(nint factory, nint queue, nint hwnd, in DxgiSwapChainDesc1 desc, out nint swapChain)
    {
        swapChain = 0;
        fixed (DxgiSwapChainDesc1* d = &desc)
        fixed (nint* output = &swapChain)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, DxgiSwapChainDesc1*, nint, nint, nint*, int>)
                SlotOf(VTableOf(factory), IDXGIFactory2_CreateSwapChainForHwnd);
            return fn(factory, queue, hwnd, d, 0, 0, output);
        }
    }

    internal static int EnumAdapterByLuid(nint factory, long luid, in Guid riid, out nint adapter)
    {
        adapter = 0;
        fixed (Guid* iid = &riid)
        fixed (nint* output = &adapter)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, long, Guid*, nint*, int>)SlotOf(VTableOf(factory), IDXGIFactory4_EnumAdapterByLuid);
            return fn(factory, luid, iid, output);
        }
    }

    // Access mask shared handles are created and opened with. GENERIC_ALL is right for a same-user
    // producer and consumer; a narrower mask buys nothing when both processes are the same principal.
    private const uint OverlaySharedSurfaceAccess = 0x10000000;

    // ---- Command queue ----

    internal static int QueueSignal(nint queue, nint fence, ulong value)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)SlotOf(VTableOf(queue), ID3D12CommandQueue_Signal);
        return fn(queue, fence, value);
    }

    internal static int QueueWait(nint queue, nint fence, ulong value)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)SlotOf(VTableOf(queue), ID3D12CommandQueue_Wait);
        return fn(queue, fence, value);
    }

    internal static void ExecuteCommandLists(nint queue, uint count, nint* lists)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)SlotOf(VTableOf(queue), ID3D12CommandQueue_ExecuteCommandLists);
        fn(queue, count, lists);
    }

    // ---- Fence ----

    internal static int FenceSetEventOnCompletion(nint fence, ulong value, nint eventHandle)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)SlotOf(VTableOf(fence), ID3D12Fence_SetEventOnCompletion);
        return fn(fence, value, eventHandle);
    }

    internal static int FenceSignal(nint fence, ulong value)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, ulong, int>)SlotOf(VTableOf(fence), ID3D12Fence_Signal);
        return fn(fence, value);
    }

    // ---- Command allocator / list ----

    internal static int CommandAllocatorReset(nint allocator)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)SlotOf(VTableOf(allocator), ID3D12CommandAllocator_Reset);
        return fn(allocator);
    }

    internal static int CommandListReset(nint list, nint allocator, nint pipelineState)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_Reset);
        return fn(list, allocator, pipelineState);
    }

    internal static int CommandListClose(nint list)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_Close);
        return fn(list);
    }

    internal static void ResourceBarrier(nint list, ResourceBarrierDesc* barriers, uint count)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, ResourceBarrierDesc*, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_ResourceBarrier);
        fn(list, count, barriers);
    }

    internal static void OMSetRenderTargets(nint list, nuint* rtvHandle)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, nuint*, int, nint, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_OMSetRenderTargets);
        fn(list, 1, rtvHandle, 0, 0);
    }

    internal static void ClearRenderTargetView(nint list, nuint rtvHandle, float* colorRgba)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nuint, float*, uint, nint, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_ClearRenderTargetView);
        fn(list, rtvHandle, colorRgba, 0, 0);
    }

    internal static void SetDescriptorHeaps(nint list, nint* heaps, uint count)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_SetDescriptorHeaps);
        fn(list, count, heaps);
    }

    internal static void SetPipelineState(nint list, nint pipelineState)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_SetPipelineState);
        fn(list, pipelineState);
    }

    internal static void SetGraphicsRootSignature(nint list, nint rootSignature)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_SetGraphicsRootSignature);
        fn(list, rootSignature);
    }

    internal static void SetGraphicsRootDescriptorTable(nint list, uint rootParameterIndex, ulong baseDescriptor)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_SetGraphicsRootDescriptorTable);
        fn(list, rootParameterIndex, baseDescriptor);
    }

    internal static void SetGraphicsRoot32BitConstants(nint list, uint rootParameterIndex, uint num32BitValues, void* data, uint offsetIn32BitValues)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, uint, void*, uint, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_SetGraphicsRoot32BitConstants);
        fn(list, rootParameterIndex, num32BitValues, data, offsetIn32BitValues);
    }

    internal static void RSSetViewports(nint list, Viewport* viewports, uint count)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_RSSetViewports);
        fn(list, count, viewports);
    }

    internal static void RSSetScissorRects(nint list, Rect* rects, uint count)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, Rect*, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_RSSetScissorRects);
        fn(list, count, rects);
    }

    internal static void IASetPrimitiveTopology(nint list, int topology)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_IASetPrimitiveTopology);
        fn(list, topology);
    }

    internal static void DrawInstanced(nint list, uint vertexCountPerInstance, uint instanceCount, uint startVertex, uint startInstance)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)SlotOf(VTableOf(list), ID3D12GraphicsCommandList_DrawInstanced);
        fn(list, vertexCountPerInstance, instanceCount, startVertex, startInstance);
    }

    // ---- Blob ----

    internal static nint BlobGetBufferPointer(nint blob)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint>)SlotOf(VTableOf(blob), ID3DBlob_GetBufferPointer);
        return fn(blob);
    }

    internal static nuint BlobGetBufferSize(nint blob)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nuint>)SlotOf(VTableOf(blob), ID3DBlob_GetBufferSize);
        return fn(blob);
    }

    // ==== Structures. Sequential and blittable; on Windows the CLR reproduces the MSVC C layout,
    //      including the padding the by-value fields imply. ====

    [StructLayout(LayoutKind.Sequential)]
    internal struct DescriptorHeapDesc
    {
        internal int Type;
        internal uint NumDescriptors;
        internal int Flags;
        internal uint NodeMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HeapProperties
    {
        internal int Type;
        internal int CpuPageProperty;
        internal int MemoryPoolPreference;
        internal uint CreationNodeMask;
        internal uint VisibleNodeMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ResourceDesc
    {
        internal int Dimension;
        internal ulong Alignment;
        internal ulong Width;
        internal uint Height;
        internal ushort DepthOrArraySize;
        internal ushort MipLevels;
        internal int Format;
        internal DxgiSampleDesc SampleDesc;
        internal int Layout;
        internal int Flags;
    }

    // Only the TRANSITION variant is ever used, so the union is modelled as its transition fields
    // inline. The size and offsets match D3D12_RESOURCE_BARRIER exactly: Type+Flags occupy eight
    // bytes, then the pointer forces eight-byte alignment for the rest.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ResourceBarrierDesc
    {
        internal int Type;
        internal int Flags;
        internal nint Resource;
        internal uint Subresource;
        internal int StateBefore;
        internal int StateAfter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Viewport
    {
        internal float TopLeftX;
        internal float TopLeftY;
        internal float Width;
        internal float Height;
        internal float MinDepth;
        internal float MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiRational
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiModeDesc
    {
        internal uint Width;
        internal uint Height;
        internal DxgiRational RefreshRate;
        internal int Format;
        internal int ScanlineOrdering;
        internal int Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiSwapChainDesc
    {
        internal DxgiModeDesc BufferDesc;
        internal DxgiSampleDesc SampleDesc;
        internal uint BufferUsage;
        internal uint BufferCount;
        internal nint OutputWindow;
        internal int Windowed;
        internal int SwapEffect;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShaderBytecode
    {
        internal nint BytecodePointer;
        internal nuint BytecodeLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamOutputDesc
    {
        internal nint SODeclaration;
        internal uint NumEntries;
        internal nint BufferStrides;
        internal uint NumStrides;
        internal uint RasterizedStream;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RenderTargetBlendDesc
    {
        internal int BlendEnable;
        internal int LogicOpEnable;
        internal int SrcBlend;
        internal int DestBlend;
        internal int BlendOp;
        internal int SrcBlendAlpha;
        internal int DestBlendAlpha;
        internal int BlendOpAlpha;
        internal int LogicOp;
        internal byte RenderTargetWriteMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendDesc
    {
        internal int AlphaToCoverageEnable;
        internal int IndependentBlendEnable;
        internal RenderTargetBlendDesc RenderTarget0;
        internal RenderTargetBlendDesc RenderTarget1;
        internal RenderTargetBlendDesc RenderTarget2;
        internal RenderTargetBlendDesc RenderTarget3;
        internal RenderTargetBlendDesc RenderTarget4;
        internal RenderTargetBlendDesc RenderTarget5;
        internal RenderTargetBlendDesc RenderTarget6;
        internal RenderTargetBlendDesc RenderTarget7;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RasterizerDesc
    {
        internal int FillMode;
        internal int CullMode;
        internal int FrontCounterClockwise;
        internal int DepthBias;
        internal float DepthBiasClamp;
        internal float SlopeScaledDepthBias;
        internal int DepthClipEnable;
        internal int MultisampleEnable;
        internal int AntialiasedLineEnable;
        internal uint ForcedSampleCount;
        internal int ConservativeRaster;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DepthStencilOpDesc
    {
        internal int StencilFailOp;
        internal int StencilDepthFailOp;
        internal int StencilPassOp;
        internal int StencilFunc;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DepthStencilDesc
    {
        internal int DepthEnable;
        internal int DepthWriteMask;
        internal int DepthFunc;
        internal int StencilEnable;
        internal byte StencilReadMask;
        internal byte StencilWriteMask;
        internal DepthStencilOpDesc FrontFace;
        internal DepthStencilOpDesc BackFace;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InputLayoutDesc
    {
        internal nint InputElementDescs;
        internal uint NumElements;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CachedPipelineState
    {
        internal nint CachedBlob;
        internal nuint CachedBlobSizeInBytes;
    }

    // Eight render-target formats inline, so the struct stays blittable without a fixed array element
    // type the marshaller would object to under AOT.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RtvFormats
    {
        internal int Format0;
        internal int Format1;
        internal int Format2;
        internal int Format3;
        internal int Format4;
        internal int Format5;
        internal int Format6;
        internal int Format7;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GraphicsPipelineStateDesc
    {
        internal nint RootSignature;
        internal ShaderBytecode VS;
        internal ShaderBytecode PS;
        internal ShaderBytecode DS;
        internal ShaderBytecode HS;
        internal ShaderBytecode GS;
        internal StreamOutputDesc StreamOutput;
        internal BlendDesc BlendState;
        internal uint SampleMask;
        internal RasterizerDesc RasterizerState;
        internal DepthStencilDesc DepthStencilState;
        internal InputLayoutDesc InputLayout;
        internal int IBStripCutValue;
        internal int PrimitiveTopologyType;
        internal uint NumRenderTargets;
        internal RtvFormats RTVFormats;
        internal int DSVFormat;
        internal DxgiSampleDesc SampleDesc;
        internal uint NodeMask;
        internal CachedPipelineState CachedPSO;
        internal int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DescriptorRange
    {
        internal int RangeType;
        internal uint NumDescriptors;
        internal uint BaseShaderRegister;
        internal uint RegisterSpace;
        internal uint OffsetInDescriptorsFromTableStart;
    }

    // A union in C: the descriptor-table variant (uint + pointer, 16 bytes) is the largest, and the
    // 32-bit-constants variant (three uints) overlaps it. Explicit layout so both can be written.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct RootParameter
    {
        [FieldOffset(0)] internal int ParameterType;

        // Descriptor-table variant.
        [FieldOffset(8)] internal uint NumDescriptorRanges;
        [FieldOffset(16)] internal nint DescriptorRanges;

        // 32-bit-constants variant, overlapping the table's storage.
        [FieldOffset(8)] internal uint Constants_ShaderRegister;
        [FieldOffset(12)] internal uint Constants_RegisterSpace;
        [FieldOffset(16)] internal uint Constants_Num32BitValues;

        [FieldOffset(24)] internal int ShaderVisibility;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StaticSamplerDesc
    {
        internal int Filter;
        internal int AddressU;
        internal int AddressV;
        internal int AddressW;
        internal float MipLODBias;
        internal uint MaxAnisotropy;
        internal int ComparisonFunc;
        internal int BorderColor;
        internal float MinLOD;
        internal float MaxLOD;
        internal uint ShaderRegister;
        internal uint RegisterSpace;
        internal int ShaderVisibility;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RootSignatureDesc
    {
        internal uint NumParameters;
        internal nint Parameters;
        internal uint NumStaticSamplers;
        internal nint StaticSamplers;
        internal int Flags;
    }
}
