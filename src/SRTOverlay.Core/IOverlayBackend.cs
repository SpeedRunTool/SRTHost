namespace SRTOverlay.Core;

/// <summary>
/// A graphics backend the shim can drive: resolve the game's entry points, hook them, let go.
/// </summary>
/// <remarks>
/// <para>
/// This exists so <see cref="OverlayRuntime"/> stays backend-agnostic, which section 11 asks for
/// explicitly. Direct3D 12 and Direct3D 11 both live inside the one shipped shim and are chosen at
/// run time, so the lifetime code cannot be written against either of them.
/// </para>
/// <para>
/// It is also what keeps the dependency direction sane: <c>SRTOverlay.Core</c> holds the injection
/// entry point and the lifetime, <c>SRTOverlay.DirectX12</c> holds everything that knows what a
/// swapchain is, and only <c>SRTOverlay.Native</c> - the publish target - references both and wires
/// one to the other.
/// </para>
/// </remarks>
public interface IOverlayBackend
{
    /// <summary>Name for the log, e.g. "Direct3D 12".</summary>
    string Name { get; }

    /// <summary>
    /// Resolve entry points and install hooks. Called once, on the overlay's own thread.
    /// </summary>
    /// <returns><see langword="false"/> if this backend is not usable in this process.</returns>
    /// <remarks>
    /// Must not throw: its caller is a step away from an unmanaged boundary where an escaping
    /// exception is a <c>FailFast</c>, and a <c>FailFast</c> here kills the game.
    /// </remarks>
    bool Initialize();

    /// <summary>Remove hooks and release resources. Safe to call when nothing was installed.</summary>
    void Shutdown();

    /// <summary>
    /// One line describing what the backend has seen since it started, for the heartbeat log.
    /// </summary>
    /// <remarks>
    /// The only way to observe a hook that draws nothing. It is read from a background thread on a
    /// timer, never from the render path - the counters it reports are written there, but formatting
    /// them is not.
    /// </remarks>
    string DescribeActivity();
}
