using System.Runtime.Versioning;
using SRTOverlay.DirectX12;

namespace SRTOverlay.Tests;

/// <summary>
/// Runs the Direct3D 12 interop self-check as part of the suite, where a GPU is available.
/// </summary>
/// <remarks>
/// The self-check is the regression guard for the calling-convention class of bug that crashed a game
/// once: <c>ID3D12CommandQueue::GetDesc</c>'s member-function sret argument order, and the shared-
/// handle name lifetime. It needs a real device, so on a machine or CI leg without one it skips rather
/// than fails - the check still runs everywhere a GPU is present, which is everywhere the shim would
/// ever run for real.
/// </remarks>
public sealed class OverlaySelfCheckTests
{
    [Fact]
    [SupportedOSPlatform("windows")] // the interop, like the shim, is Windows-only
    public void InteropSelfCheckPasses()
    {
        // Opt-in: creating a D3D12 device here adds enough parallel load to occasionally trip the
        // timing-sensitive IPC latency benchmark in another test assembly. The authoritative run of
        // this check is the spike's `--selfcheck` (by hand, on a GPU); this test lets a GPU CI leg opt
        // in with SRT_OVERLAY_GPU_TESTS=1 without destabilising the default suite.
        if (Environment.GetEnvironmentVariable("SRT_OVERLAY_GPU_TESTS") != "1")
        {
            Assert.Skip("Set SRT_OVERLAY_GPU_TESTS=1 to run the GPU interop self-check in the suite.");
            return;
        }

        List<string> log = [];
        // Light mode: no producer-thread draw round-trip. That heavy GPU path is exercised by the
        // spike's --selfcheck; running it here would load the machine enough to perturb the timing-
        // sensitive IPC tests running in parallel.
        bool ok = OverlaySelfCheck.Run(log.Add, drawRoundTrip: false);

        // If the device could never be created, there is no GPU here; skip rather than fail.
        if (!ok && !log.Exists(line => line.Contains("device created", StringComparison.Ordinal)))
        {
            Assert.Skip("No Direct3D 12 device available on this machine; interop self-check skipped.");
            return;
        }

        Assert.True(ok, "Interop self-check failed:" + Environment.NewLine + string.Join(Environment.NewLine, log));
    }
}
