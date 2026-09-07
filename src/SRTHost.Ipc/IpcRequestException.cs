using SRTPluginBase.Abstractions;

namespace SRTHost.Ipc;

/// <summary>
/// The peer answered a request with <see cref="ErrorMessage"/>.
/// </summary>
/// <remarks>
/// Distinct from <see cref="IpcProtocolException"/>, and the distinction is the one the supervisor
/// acts on: this means the connection is healthy and the operation failed, so retrying or reporting
/// it to the user is sensible. A protocol exception means the stream itself can no longer be
/// trusted and the runner has to be recycled.
/// </remarks>
public sealed class IpcRequestException : Exception
{
    /// <summary>Creates an exception from the error the peer sent.</summary>
    public IpcRequestException(ErrorMessage error)
        : base(error?.Message ?? "The peer reported an unspecified error.")
    {
        ArgumentNullException.ThrowIfNull(error);

        SubStatus = error.SubStatus;
        Detail = error.Detail;
    }

    /// <summary>How the peer classified the failure, when it could.</summary>
    public PluginSubStatus SubStatus { get; }

    /// <summary>Stack trace or other detail from the peer.</summary>
    public string? Detail { get; }
}
