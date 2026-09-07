using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SRTPluginBase.Abstractions;

namespace SRTHost.Ipc;

/// <summary>
/// Handles an inbound control message. Returning a message sends it back correlated to the request;
/// returning null sends nothing.
/// </summary>
public delegate ValueTask<IpcMessage?> ControlMessageHandler(
    IpcMessage message,
    uint correlationId,
    CancellationToken cancellationToken);

/// <summary>
/// Handles an inbound data frame. The payload is valid only until the returned task completes.
/// </summary>
public delegate ValueTask DataFrameHandler(
    DataFrameHeader header,
    ReadOnlySequence<byte> payload,
    CancellationToken cancellationToken);

/// <summary>
/// Handles an inbound log record.
/// </summary>
public delegate ValueTask LogRecordHandler(IpcLogRecord record, CancellationToken cancellationToken);

/// <summary>
/// One duplex connection between the router and a runner: framing, correlation and dispatch.
/// </summary>
/// <remarks>
/// Symmetric by design - the same type drives both ends. The two differ only in which pipe stream
/// they were handed and which handlers they install, which keeps the protocol honest: there is one
/// implementation of correlation, timeouts and teardown rather than a router copy and a runner copy
/// that drift.
/// </remarks>
public sealed class IpcConnection : IAsyncDisposable
{
    /// <summary>How long a request waits before it is abandoned.</summary>
    /// <remarks>
    /// Generous, because the thing on the other end is a plugin doing arbitrary work - attaching to a
    /// game, scanning memory - not a service with a latency budget. The supervisor's heartbeat is
    /// what detects a hung runner; this only stops a caller waiting forever.
    /// </remarks>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for <see cref="LoadPluginMessage"/>, which does markedly more work than anything else.
    /// </summary>
    /// <remarks>
    /// Loading pulls an assembly and its whole private dependency closure off disk, and the first one
    /// after an update lands on a cold file cache with an antivirus scanner in the way. Thirty
    /// seconds is not enough for that on a bad day, and a spurious timeout here would look exactly
    /// like a broken plugin.
    /// </remarks>
    public static readonly TimeSpan LoadRequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Buffer size for the Pipelines reader and writer sitting on the pipe stream.
    /// </summary>
    /// <remarks>
    /// The defaults are 4 KiB on both, which is smaller than a single payload frame - so every frame
    /// cost several stream reads and spanned several write segments. Raising this was worth roughly
    /// 3.5x on the loopback benchmark, from 15,900 to 55,000 frames a second at 10 KiB a frame, and
    /// halved p99 delivery latency. It was invisible until measured.
    /// <para>
    /// The cost is per connection, not per message: the writer rents one segment from the pool and
    /// refills it, rather than one per frame. Two of these plus the kernel's own pipe buffers is the
    /// per-plugin memory price of the out-of-process design.
    /// </para>
    /// </remarks>
    private const int StreamBufferSize = 256 * 1024;

    private readonly Stream stream;
    private readonly PipeReader reader;
    private readonly PipeWriter pipeWriter;
    private readonly FrameReader frameReader;
    private readonly FrameWriter frameWriter;

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IpcMessage>> pending = new();
    private readonly CancellationTokenSource lifetime = new();

    private int nextCorrelationId;
    private int disposed;

    /// <summary>Creates a connection over an already-connected duplex stream.</summary>
    public IpcConnection(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        this.stream = stream;

        reader = PipeReader.Create(
            stream,
            new StreamPipeReaderOptions(bufferSize: StreamBufferSize, minimumReadSize: StreamBufferSize / 4, leaveOpen: true));

        pipeWriter = PipeWriter.Create(
            stream,
            new StreamPipeWriterOptions(minimumBufferSize: StreamBufferSize, leaveOpen: true));

        frameReader = new FrameReader(reader);
        frameWriter = new FrameWriter(pipeWriter);
    }

    /// <summary>Invoked for each inbound control message that is not a response to a pending request.</summary>
    public ControlMessageHandler? OnControlMessage { get; init; }

    /// <summary>Invoked for each inbound data frame.</summary>
    public DataFrameHandler? OnDataFrame { get; init; }

    /// <summary>Invoked for each inbound log record.</summary>
    public LogRecordHandler? OnLogRecord { get; init; }

    /// <summary>
    /// Runs the read loop until the peer disconnects, a protocol violation occurs, or
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);

        try
        {
            await frameReader.ReadAllAsync(DispatchAsync, linked.Token).ConfigureAwait(false);
            FailPending(new IpcProtocolException("The peer disconnected."));
        }
        catch (Exception ex)
        {
            // Every in-flight request is waiting on a reply that is now never coming. Failing them
            // here rather than letting each time out individually turns a dead connection into an
            // immediate error at every call site instead of a 30 second stall at each of them.
            FailPending(ex);
            throw;
        }
    }

    /// <summary>Sends a message and does not wait for a reply.</summary>
    public ValueTask SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
        => SendAsync(message, correlationId: 0, cancellationToken);

    /// <summary>Sends a message correlated to a request that was received.</summary>
    public async ValueTask SendAsync(IpcMessage message, uint correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJsonContext.Default.IpcMessage);

        await frameWriter.WriteAsync(
            new FrameHeader
            {
                Channel = IpcChannel.Control,
                Codec = PayloadCodec.Json,
                MessageKind = message.Kind,
                CorrelationId = correlationId,
            },
            json,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request and waits for the correlated reply.
    /// </summary>
    /// <exception cref="TimeoutException">No reply arrived in time.</exception>
    /// <exception cref="IpcRequestException">The peer answered with <see cref="ErrorMessage"/>.</exception>
    public async Task<IpcMessage> RequestAsync(
        IpcMessage message,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Zero is reserved for "this is a notification", so skip it on wraparound rather than
        // letting one request in four billion be silently uncorrelatable.
        uint correlationId;

        do
        {
            correlationId = unchecked((uint)Interlocked.Increment(ref nextCorrelationId));
        }
        while (correlationId == 0);

        TaskCompletionSource<IpcMessage> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!pending.TryAdd(correlationId, completion))
            throw new InvalidOperationException($"Correlation id {correlationId} is already in flight.");

        try
        {
            await SendAsync(message, correlationId, cancellationToken).ConfigureAwait(false);

            TimeSpan effective = timeout
                ?? (message is LoadPluginMessage ? LoadRequestTimeout : DefaultRequestTimeout);

            IpcMessage reply = await completion.Task
                .WaitAsync(effective, cancellationToken)
                .ConfigureAwait(false);

            return reply is ErrorMessage error
                ? throw new IpcRequestException(error)
                : reply;
        }
        finally
        {
            pending.TryRemove(correlationId, out _);
        }
    }

    /// <summary>Publishes a payload on the data channel.</summary>
    /// <remarks>
    /// The payload is written straight through from whatever buffer it arrived in; nothing here
    /// inspects or copies it.
    /// </remarks>
    public async ValueTask SendDataAsync(
        DataFrameHeader header,
        ReadOnlySequence<byte> payload,
        PayloadCodec codec = PayloadCodec.Json,
        CancellationToken cancellationToken = default)
    {
        int preambleSize = header.EncodedSize;
        byte[] preamble = ArrayPool<byte>.Shared.Rent(preambleSize);

        try
        {
            header.Write(preamble);

            // One frame, two source buffers: the preamble this method owns and the caller's payload.
            // Concatenating them into a sequence keeps the payload from being copied on the way out.
            ReadOnlySequence<byte> frame = Concat(preamble.AsMemory(0, preambleSize), payload);

            await frameWriter.WriteAsync(
                new FrameHeader { Channel = IpcChannel.Data, Codec = codec },
                frame,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(preamble);
        }
    }

    /// <summary>Forwards a log record to the peer.</summary>
    public async ValueTask SendLogAsync(IpcLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, IpcJsonContext.Default.IpcLogRecord);

        await frameWriter.WriteAsync(
            new FrameHeader { Channel = IpcChannel.Log, Codec = PayloadCodec.Json },
            json,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DispatchAsync(
        FrameHeader header,
        ReadOnlySequence<byte> payload,
        CancellationToken cancellationToken)
    {
        switch (header.Channel)
        {
            case IpcChannel.Control:
                await DispatchControlAsync(header, payload, cancellationToken).ConfigureAwait(false);
                break;

            case IpcChannel.Data when OnDataFrame is not null:
                DataFrameHeader dataHeader = DataFrameHeader.Read(payload, out ReadOnlySequence<byte> body);
                await OnDataFrame(dataHeader, body, cancellationToken).ConfigureAwait(false);
                break;

            case IpcChannel.Log when OnLogRecord is not null:
                IpcLogRecord? record = Deserialize(payload, IpcJsonContext.Default.IpcLogRecord);

                if (record is not null)
                    await OnLogRecord(record, cancellationToken).ConfigureAwait(false);

                break;

            default:
                // A frame on a channel nobody installed a handler for is dropped rather than fatal.
                // The alternative would make installing handlers order-dependent at startup, and a
                // runner that logs before the router has wired up its log sink is not a protocol
                // violation.
                break;
        }
    }

    private async ValueTask DispatchControlAsync(
        FrameHeader header,
        ReadOnlySequence<byte> payload,
        CancellationToken cancellationToken)
    {
        IpcMessage message = Deserialize(payload, IpcJsonContext.Default.IpcMessage)
            ?? throw new IpcProtocolException($"Control frame of kind {header.MessageKind} deserialised to null.");

        if (header.CorrelationId != 0
            && pending.TryRemove(header.CorrelationId, out TaskCompletionSource<IpcMessage>? completion))
        {
            completion.TrySetResult(message);
            return;
        }

        if (OnControlMessage is null)
            return;

        IpcMessage? reply = await OnControlMessage(message, header.CorrelationId, cancellationToken).ConfigureAwait(false);

        if (reply is not null && header.CorrelationId != 0)
            await SendAsync(reply, header.CorrelationId, cancellationToken).ConfigureAwait(false);
    }

    private static T? Deserialize<T>(in ReadOnlySequence<byte> payload, JsonTypeInfo<T> typeInfo)
    {
        Utf8JsonReader jsonReader = new(payload);

        try
        {
            return JsonSerializer.Deserialize(ref jsonReader, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException("Control frame carried malformed JSON.", ex);
        }
    }

    private static ReadOnlySequence<byte> Concat(ReadOnlyMemory<byte> first, in ReadOnlySequence<byte> rest)
    {
        MemorySegment head = new(first);
        MemorySegment tail = head;

        foreach (ReadOnlyMemory<byte> segment in rest)
            tail = tail.Append(segment);

        return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
    }

    private void FailPending(Exception cause)
    {
        foreach (KeyValuePair<uint, TaskCompletionSource<IpcMessage>> entry in pending)
        {
            if (pending.TryRemove(entry.Key, out TaskCompletionSource<IpcMessage>? completion))
                completion.TrySetException(cause);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await lifetime.CancelAsync().ConfigureAwait(false);
        FailPending(new ObjectDisposedException(nameof(IpcConnection)));

        await reader.CompleteAsync().ConfigureAwait(false);
        await pipeWriter.CompleteAsync().ConfigureAwait(false);

        frameWriter.Dispose();
        lifetime.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Links buffers into one sequence without copying their contents.</summary>
    private sealed class MemorySegment : ReadOnlySequenceSegment<byte>
    {
        public MemorySegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public MemorySegment Append(ReadOnlyMemory<byte> memory)
        {
            MemorySegment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
