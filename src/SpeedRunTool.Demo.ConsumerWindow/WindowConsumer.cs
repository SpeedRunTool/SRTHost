using System.Runtime.Versioning;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using SRTPluginBase.Abstractions;
using SpeedRunTool.Demo.Contracts;

[assembly: SrtPluginAssembly(
    "SpeedRunTool.Demo.ConsumerWindow",
    typeof(SpeedRunTool.Demo.ConsumerWindow.WindowConsumer))]

namespace SpeedRunTool.Demo.ConsumerWindow;

/// <summary>
/// Shows the demo producer's payload in a window of its own.
/// </summary>
/// <remarks>
/// The migration reference for the WinForms consumer windows of the previous generation - the RE1,
/// RE2 and RE3 label grids a user dragged to a second monitor. Dropping the <c>-windows</c> target
/// framework means those cannot be hosted any more (nothing in a plain <c>net10.0</c> process can
/// resolve <c>System.Windows.Forms</c>), and this is what they become: the same grid of labels,
/// bound instead of assigned, in an Avalonia window the plugin owns.
/// <para>
/// It is also the proof that a runner process can own a UI thread and a window at all, which is the
/// capability an overlay needs.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowConsumer : ConsumerPluginBase<DemoPayload>
{
    private readonly PayloadViewModel viewModel = new();

    private AvaloniaUiThread? ui;
    private PayloadWindow? window;

    /// <inheritdoc />
    public override IPluginInfo Info { get; } =
        PluginInfo.FromAssembly(typeof(WindowConsumer).Assembly);

    /// <inheritdoc />
    public override IReadOnlyList<ChannelSubscription> Subscriptions { get; } =
    [
        new ChannelSubscription
        {
            ChannelId = DemoChannel.Id,
            MinimumContractVersion = DemoChannel.ContractVersion,
        },
    ];

    /// <inheritdoc />
    protected override JsonTypeInfo<DemoPayload> PayloadTypeInfo => DemoPayloadJsonContext.Default.DemoPayload;

    /// <inheritdoc />
    /// <remarks>
    /// The window opens in <see cref="StartAsync"/> rather than here. Initialise is for setup that
    /// must succeed before the host calls a plugin loaded; showing a window is work, and a plugin the
    /// user has stopped should not have one on screen.
    /// </remarks>
    public override async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);

        ui = new AvaloniaUiThread($"{Info.Id} UI");

        await ui.InvokeAsync(
            () =>
            {
                window = new PayloadWindow(viewModel);
                window.Show();
            },
            cancellationToken).ConfigureAwait(false);

        Logger.LogInformation("Window opened on {Thread}.", $"{Info.Id} UI");
    }

    /// <inheritdoc />
    public override async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await CloseWindowAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask OnPayloadAsync(DemoPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Posted, not awaited. The runner calls this on its dispatch loop about thirty times a
        // second and expects it back promptly; waiting for a repaint here would make a slow frame
        // into a slow consumer. The payload has already been deserialised by the base class into an
        // object this plugin owns, so it is safe to hand to another thread - the pooled BYTES are
        // what must not outlive this call.
        ui?.Post(() => viewModel.Update(payload));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask OnChannelClosedAsync(string channelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        // The window stays; the values are blanked. The producer may well come back - a game
        // restarting is the ordinary case - and a window that vanished and reappeared would move
        // itself back to the middle of the primary monitor every time.
        ui?.Post(() => viewModel.Clear(channelId));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await CloseWindowAsync().ConfigureAwait(false);

    private async ValueTask CloseWindowAsync()
    {
        AvaloniaUiThread? thread = ui;

        if (thread is null)
            return;

        ui = null;

        try
        {
            // A short timeout of its own: shutdown must not hang on a UI thread that is wedged, and
            // the runner is about to exit anyway.
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));

            await thread.InvokeAsync(
                () =>
                {
                    if (window is null)
                        return;

                    window.ClosingForShutdown = true;
                    window.Close();
                    window = null;
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            Logger.LogWarning("The UI thread did not close its window in time; disposing anyway.");
        }

        await thread.DisposeAsync().ConfigureAwait(false);
    }
}
