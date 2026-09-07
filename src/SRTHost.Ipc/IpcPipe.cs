using System.IO.Pipes;

namespace SRTHost.Ipc;

/// <summary>
/// Creates the named pipe endpoints the router and its runners talk over.
/// </summary>
public static class IpcPipe
{
    /// <summary>
    /// Kernel buffer reserved on each direction of a pipe.
    /// </summary>
    /// <remarks>
    /// Windows defaults this to a few kilobytes, which is smaller than a single payload frame - so
    /// every write blocked until the reader drained it, turning a stream into a lockstep ping-pong.
    /// This lets a sender run roughly twenty-five frames ahead, which is what a burst after a briefly
    /// stalled reader looks like.
    /// <para>
    /// Not larger, deliberately: this is non-paged kernel memory reserved per direction per pipe, and
    /// there is one pipe per plugin. A megabyte measured no faster than this on the loopback
    /// benchmark, so the extra reservation bought nothing.
    /// </para>
    /// </remarks>
    public const int BufferSize = 256 * 1024;

    /// <summary>
    /// Creates the router's end of a pipe.
    /// </summary>
    /// <remarks>
    /// Three options carry weight here.
    /// <para>
    /// <see cref="PipeOptions.FirstPipeInstance"/> makes an already-taken name a hard failure. Without
    /// it, Windows would happily hand back another instance of a pipe somebody else created - a
    /// process squatting the name ahead of the host would then be handed a runner's connection, and
    /// with it the plugin's data and its control channel. Failing to start is the correct response.
    /// </para>
    /// <para>
    /// <see cref="PipeOptions.CurrentUserOnly"/> puts an ACL on the pipe granting only the creating
    /// user, and makes the client verify the server's owner SID. Both halves matter, and both are
    /// exactly right for an install that lives in <c>%LOCALAPPDATA%</c> and runs as one user.
    /// </para>
    /// <para>
    /// One instance per name, because a name identifies one plugin. A second connection to it would
    /// be a second runner for the same plugin, which is never legitimate.
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">The name is already in use.</exception>
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
            inBufferSize: BufferSize,
            outBufferSize: BufferSize);
    }

    /// <summary>
    /// Creates a runner's end of a pipe.
    /// </summary>
    /// <remarks>
    /// Byte mode, matching the server. Message mode would give framing for free, but it caps a
    /// message at the pipe buffer and makes a partial read a lossy operation - the frame codec is
    /// both cheaper and honest about fragmentation.
    /// </remarks>
    public static NamedPipeClientStream CreateClient(string pipeName, string serverName = ".")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        return new NamedPipeClientStream(
            serverName,
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    /// <summary>
    /// Connects a client, giving the router time to have created its end.
    /// </summary>
    /// <remarks>
    /// A runner is spawned by the router, so the server almost always exists first - but process
    /// startup is not ordered against pipe creation, and a runner that raced ahead and failed
    /// instantly would look identical to a plugin that cannot load. The timeout is the runner's own
    /// startup budget, not a health check.
    /// </remarks>
    public static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream client = CreateClient(pipeName);

        try
        {
            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(timeout);

            await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException($"No pipe named '{pipeName}' accepted a connection within {timeout}.");
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
