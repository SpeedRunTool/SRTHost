using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Threading;

namespace SpeedRunTool.Demo.ConsumerWindow;

/// <summary>
/// Owns an Avalonia application and its UI thread inside a plugin runner process.
/// </summary>
/// <remarks>
/// This is the piece a WinForms plugin author is looking for, and the reason it is not one line.
/// <para>
/// <c>RequiresUiThread</c> is deliberately <see langword="false"/> for this
/// plugin. That flag asks the runner for a bare Win32 <c>GetMessage</c> pump on an STA thread, which
/// is what an in-frame overlay wants; Avalonia brings a message loop of its own, and the two cannot
/// share a thread. The runner wakes its pump with <c>PostThreadMessage</c>, and thread messages -
/// having no window - are dropped by Avalonia's loop, so every call the runner marshalled onto that
/// thread would queue and never run. So Avalonia gets a thread to itself and the runner keeps
/// calling this plugin on its own dispatch loop.
/// </para>
/// <para>
/// One Avalonia application per process is the framework's rule, which is satisfied here for free:
/// the host gives every plugin its own runner process. Two windowed plugins are two processes.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class AvaloniaUiThread : IAsyncDisposable
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Thread thread;

    /// <summary>Starts the UI thread. Await <see cref="Ready"/> before touching the dispatcher.</summary>
    public AvaloniaUiThread(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        thread = new Thread(Run)
        {
            Name = name,

            // Not a background thread: DisposeAsync stops the loop and joins, so windows are
            // destroyed on the thread that created them. A background thread torn down at process
            // exit skips every close handler and leaves the graphics device to the operating system.
            IsBackground = false,
        };

        // STA is what a window-owning thread on Windows is expected to be - the common file dialogs
        // and OLE drag-and-drop require it, and a plugin that opens one from an MTA thread fails in a
        // way its author cannot diagnose.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Completes once Avalonia is initialised and the dispatcher is running.</summary>
    /// <remarks>Faults if Avalonia could not start, so a headless machine reports rather than hangs.</remarks>
    public Task Ready => ready.Task;

    /// <summary>Runs <paramref name="work"/> on the UI thread.</summary>
    public async ValueTask InvokeAsync(Action work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(work).GetTask().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues <paramref name="work"/> on the UI thread without waiting for it.</summary>
    /// <remarks>
    /// What a payload handler wants. A consumer is called about thirty times a second and must return
    /// promptly; blocking on a render would turn every dropped frame into visible lag, and the
    /// newest payload is the only one worth drawing anyway.
    /// </remarks>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (!ready.Task.IsCompletedSuccessfully)
            return;

        Dispatcher.UIThread.Post(work, DispatcherPriority.Background);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);

        // Joined off the caller's thread: the runner calls DisposeAsync from its dispatch loop, and
        // the UI thread may still be draining posted work that needs it.
        await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

        shutdown.Dispose();
    }

    private void Run()
    {
        try
        {
            // Two deliberate choices, both about not being an application entry point.
            //
            // UseWin32().UseSkia().UseHarfBuzz() rather than UsePlatformDetect(), which lives in
            // Avalonia.Desktop: that package exists to wire up an entry point, and naming the
            // back-ends is both what this plugin actually needs and one fewer package in the plugin
            // folder. UseHarfBuzz is not optional and is easy to forget - Skia renders glyphs but
            // does not shape text, so without it Setup() throws "No text shaping system
            // configured" and the plugin faults at start with nothing on screen.
            //
            // SetupWithoutStarting rather than one of the StartWith* helpers: those own the thread's
            // lifetime and exit the process when the last window closes. Here the process belongs to
            // the runner.
            AppBuilder.Configure<PayloadApp>()
                .UseWin32()
                .UseSkia()
                .UseHarfBuzz()
                .SetupWithoutStarting();

            ready.TrySetResult();

            // Blocks until the token is cancelled. Avalonia's own loop, pumping Win32 messages and
            // dispatcher work for the windows created on this thread.
            Dispatcher.UIThread.MainLoop(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            // Surfaced through Ready so InitializeAsync fails with the real reason. A UI thread that
            // died silently would leave the plugin Running with no window and nothing in the log.
            ready.TrySetException(ex);
        }
    }
}
