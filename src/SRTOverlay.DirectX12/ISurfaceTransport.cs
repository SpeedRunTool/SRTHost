using SRTOverlay.Protocol;

namespace SRTOverlay.DirectX12;

/// <summary>
/// How a <see cref="SharedSurfaceProducer"/> learns which surface to make and publishes each frame it
/// completes - the negotiation half, kept separate from the Direct3D 12 half so the same graphics loop
/// can be driven either over the runner-to-shim pipe (spike step 5) or over the step-3 shared-memory
/// control blocks (retained for the superseded C# shim).
/// </summary>
/// <remarks>
/// The textures and the fence themselves never travel this interface: they cross by name, created by
/// the producer and opened by the shim, regardless of which transport carries the negotiation. This
/// interface carries only the two messages that used to be the request and publish blocks.
/// </remarks>
public interface ISurfaceTransport : IDisposable
{
    /// <summary>
    /// Block until the shim asks for a surface, or <paramref name="cancellation"/> fires.
    /// </summary>
    /// <returns><see langword="false"/> if cancelled before a request arrived.</returns>
    bool TryGetRequest(CancellationToken cancellation, out SurfaceRequest request);

    /// <summary>
    /// Announce that the surface now exists (<paramref name="alive"/> true) or is going away (false).
    /// </summary>
    /// <remarks>
    /// The shim composites only while the consumer is alive, so this brackets the animation: true once
    /// the textures and fence are created, false as the producer drains and shuts down.
    /// </remarks>
    void SetAlive(bool alive);

    /// <summary>
    /// Publish the newest complete texture index and the fence value its queue will signal when that
    /// texture is done. Called once per completed frame.
    /// </summary>
    void Publish(uint latestIndex, ulong fenceValue);
}
