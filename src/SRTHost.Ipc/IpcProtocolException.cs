namespace SRTHost.Ipc;

/// <summary>
/// The peer sent something that does not conform to the frame protocol.
/// </summary>
/// <remarks>
/// Always fatal to the connection, never to a single frame. Framing is a byte stream with no
/// resynchronisation marker, so once a header does not parse there is no way to find where the next
/// one begins - continuing would interpret payload bytes as headers. The caller's only correct
/// response is to tear the connection down; for a runner that means the supervisor restarts it,
/// which is a recovery path that already exists and is already tested.
/// </remarks>
public sealed class IpcProtocolException : Exception
{
    /// <summary>Creates an exception with a message describing the violation.</summary>
    public IpcProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception wrapping an underlying failure.</summary>
    public IpcProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
