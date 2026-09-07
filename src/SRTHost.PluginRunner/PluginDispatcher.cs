using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SRTHost.PluginRunner;

/// <summary>
/// Decides which thread a plugin's own code runs on.
/// </summary>
/// <remarks>
/// Every call the runner makes into a plugin - initialise, start, produce, consume, stop, dispose -
/// goes through here, so that a plugin needing a UI thread gets one consistently instead of getting
/// one for the calls somebody remembered and a thread-pool thread for the rest. That inconsistency
/// is the shape of the hardest bug in an overlay: it works, and then one day a window handle is
/// created on a pool thread and the whole thing deadlocks on a message it will never pump.
/// </remarks>
internal interface IPluginDispatcher : IAsyncDisposable
{
    /// <summary>Runs <paramref name="work"/> on the plugin's thread and waits for it to finish.</summary>
    ValueTask InvokeAsync(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="work"/> on the plugin's thread and returns its result.</summary>
    ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> work,
        CancellationToken cancellationToken);
}

/// <summary>
/// The dispatcher for an ordinary headless plugin: none at all.
/// </summary>
/// <remarks>
/// A producer reading memory has no thread affinity, and putting one behind a queue would cost a
/// context switch per tick for nothing. The plan's "headless plugins pay nothing" is this class.
/// </remarks>
internal sealed class InlinePluginDispatcher : IPluginDispatcher
{
    /// <summary>The single instance; it holds no state.</summary>
    public static readonly InlinePluginDispatcher Instance = new();

    private InlinePluginDispatcher()
    {
    }

    /// <inheritdoc />
    public ValueTask InvokeAsync(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A dedicated STA thread running a Win32 message loop, for plugins that declare
/// <see cref="SRTPluginBase.Abstractions.IPluginInfo.RequiresUiThread"/>.
/// </summary>
/// <remarks>
/// A plugin that opens a window or draws a Direct2D overlay needs a thread that owns those handles
/// and pumps their messages forever. Nothing in the runner otherwise provides one: the main thread
/// is blocked in the IPC read loop and everything else is a thread-pool thread that may service a
/// different work item on its next turn.
/// <para>
/// The loop is the plain Win32 one rather than a WinForms or WPF pump, because the runner targets
/// <c>net10.0</c> with no <c>-windows</c> TFM and so cannot reference either - the same constraint
/// that makes generation 1's WinForms consumer windows unsupported. What a plugin actually needs
/// from a pump is <c>GetMessage</c> and <c>DispatchMessage</c>, which is here in full.
/// </para>
/// <para>
/// A <see cref="SynchronizationContext"/> is installed on the thread, so an <c>await</c> inside
/// plugin code resumes on the UI thread rather than escaping to the pool. Without it, only the first
/// segment of an async plugin method would be correctly affinitised - which is precisely the
/// intermittent failure this class exists to prevent.
/// </para>
/// <para>
/// Windows-only, and annotated as such rather than papered over. A message pump is not a portable
/// concept, and the runner's other half is: a headless producer built for a future Linux port has no
/// reason to lose that portability because this type exists in the same assembly.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class UiThreadDispatcher : IPluginDispatcher
{
    /// <summary>
    /// The message posted to wake the loop when work is queued. <c>WM_APP</c> and above is the range
    /// reserved for an application's own messages, so nothing else can be using it.
    /// </summary>
    private const uint WmSrtWork = 0x8000 + 1;

    private const uint WmQuit = 0x0012;

    private readonly ConcurrentQueue<Action> work = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;

    private uint threadId;
    private int disposed;

    /// <summary>Starts the thread and its message loop.</summary>
    /// <param name="name">Thread name, so the plugin is identifiable in a debugger and a dump.</param>
    public UiThreadDispatcher(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        thread = new Thread(Pump)
        {
            Name = name,

            // Not background. The loop is shut down explicitly through DisposeAsync so the plugin's
            // window is destroyed on the thread that created it; letting the runtime tear a
            // background thread down at process exit skips every WM_DESTROY and leaves DirectX
            // resources to be reclaimed by the operating system instead of released.
            IsBackground = false,
        };

        // STA because that is what a window-owning thread on Windows is expected to be: OLE drag and
        // drop, the common file dialogs and most shell interfaces require it, and a plugin that
        // opens any of them from an MTA thread fails in a way its author cannot diagnose.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Completes once the loop is running and able to accept work.</summary>
    public Task Ready => ready.Task;

    /// <inheritdoc />
    public async ValueTask InvokeAsync(Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        await InvokeAsync<object?>(
            async token =>
            {
                await callback(token).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(disposed != 0, this);

        await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);

        TaskCompletionSource<TResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Post(async () =>
        {
            try
            {
                // ConfigureAwait(true) on purpose: the synchronization context installed below is
                // what brings the continuation back to this thread, and that is the entire point.
                completion.TrySetResult(await callback(cancellationToken).ConfigureAwait(true));
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Queues <paramref name="action"/> onto the UI thread without waiting for it.</summary>
    private void Post(Action action)
    {
        work.Enqueue(action);

        uint target = Volatile.Read(ref threadId);

        if (target != 0)
            _ = PostThreadMessage(target, WmSrtWork, UIntPtr.Zero, IntPtr.Zero);
    }

    private void Pump()
    {
        try
        {
            // Forces the thread to own a message queue before anything is posted to it.
            // PostThreadMessage silently fails against a thread that has never called a message
            // function, and the resulting symptom - work queued, never run, no error anywhere - is
            // extremely hard to read backwards. The id is published only afterwards, so no post can
            // reach the window between the two.
            _ = PeekMessage(out _, IntPtr.Zero, WmSrtWork, WmSrtWork, PeekMessageRemove.NoRemove);

            SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(this));

            Volatile.Write(ref threadId, GetCurrentThreadId());
            ready.TrySetResult();

            // Anything queued before the id was published got no wake message, so drain once before
            // blocking in GetMessage.
            Drain();

            while (true)
            {
                int result = GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0);

                // 0 is WM_QUIT and -1 is an error; both end the loop. An error here means the
                // message queue itself is broken, which nothing in this process can recover from.
                if (result is 0 or (-1))
                    break;

                if (message.Message == WmSrtWork)
                {
                    Drain();
                    continue;
                }

                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }

            // Anything queued between the last drain and WM_QUIT still has a caller awaiting it.
            Drain();
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
        finally
        {
            ready.TrySetResult();
        }
    }

    private void Drain()
    {
        while (work.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // A work item captures its own exceptions into its completion source, so reaching
                // here means the queueing machinery itself failed. Losing the pump would strand
                // every later call, so report and carry on.
                Console.Error.WriteLine($"UI thread work item failed: {ex}");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        uint target = Volatile.Read(ref threadId);

        if (target != 0)
            _ = PostThreadMessage(target, WmQuit, UIntPtr.Zero, IntPtr.Zero);

        // Joined off the calling thread: this is awaited from the IPC read loop during shutdown, and
        // blocking that thread while the plugin's own teardown may still need to talk to the host
        // is a deadlock waiting to be discovered by somebody else.
        await Task.Run(() =>
        {
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                Console.Error.WriteLine("UI thread did not exit within 5 seconds; abandoning it.");
        }).ConfigureAwait(false);
    }

    /// <summary>Marshals continuations back onto the UI thread.</summary>
    private sealed class UiSynchronizationContext(UiThreadDispatcher dispatcher) : SynchronizationContext
    {
        /// <inheritdoc />
        public override void Post(SendOrPostCallback callback, object? state)
            => dispatcher.Post(() => callback(state));

        /// <inheritdoc />
        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Environment.CurrentManagedThreadId == dispatcher.thread.ManagedThreadId)
            {
                callback(state);
                return;
            }

            using ManualResetEventSlim done = new();

            dispatcher.Post(() =>
            {
                try
                {
                    callback(state);
                }
                finally
                {
                    done.Set();
                }
            });

            done.Wait();
        }

        /// <inheritdoc />
        public override SynchronizationContext CreateCopy() => this;
    }

    #region Win32

    private enum PeekMessageRemove : uint
    {
        NoRemove = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint filterMin,
        uint filterMax,
        PeekMessageRemove remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(ref NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    #endregion
}
