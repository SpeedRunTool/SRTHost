using System.Diagnostics;
using SRTOverlay.Core;
using SRTOverlay.Protocol;

namespace SRTOverlay.Tests;

/// <summary>
/// The startup blob, and every way the shim can refuse to start.
/// </summary>
/// <remarks>
/// <para>
/// These run in the test process, which is exactly how they run in a game: the shim's start path is
/// ordinary managed code, and only the thread it arrives on is unusual. So this covers the refusals
/// that section 11 asks for - a mis-aimed injection stopping itself, and a substituted blob being
/// rejected - without needing a game. The blob is the packed struct the C++ shim reads (via
/// <see cref="OverlayStartupOptions.ToBlob"/>), so these also pin the C# side of that shared layout.
/// </para>
/// <para>
/// <see cref="OverlayRuntime"/> is static and latches on start, so every test here stops it again.
/// That is not a testing wart: a second injection into a process that already has the overlay must
/// be a no-op rather than a second set of hooks, and these tests are what pins that.
/// </para>
/// </remarks>
[Collection(nameof(OverlayStartupTests))]
public sealed class OverlayStartupTests : IDisposable
{
    /// <summary>The process the tests are running in, which is the only one they can honestly name.</summary>
    private static readonly string ThisProcess = Path.GetFileName(Environment.ProcessPath!);

    public void Dispose() => OverlayRuntime.Stop();

    private static OverlayStartupOptions Valid() => new()
    {
        ProtocolVersion = OverlayProtocol.Version,
        TargetProcess = ThisProcess,
        LogPath = string.Empty,
        SessionId = "tests",
    };

    [Fact]
    public void TheBlobIsTheContractSize()
    {
        // The one number the C# mirror and the C++ shim's packed struct must agree on. If a field is
        // added or a buffer resized on one side and not the other, the wire breaks silently - this is
        // the C# half of the guard (the C++ half is a static_assert on sizeof).
        Assert.Equal(1696, OverlayStartupOptions.BlobSize);
    }

    [Fact]
    public void StartsWhenTheBlobNamesThisProcess()
    {
        Assert.Equal(OverlayStartResult.Success, OverlayRuntime.Start(Valid().ToBlob()));
        Assert.NotNull(OverlayRuntime.Options);
        Assert.Equal(ThisProcess, OverlayRuntime.Options!.TargetProcess);
    }

    [Fact]
    public void RefusesAProcessTheBlobDidNotName()
    {
        OverlayStartupOptions elsewhere = Valid() with { TargetProcess = "some-other-game.exe" };

        Assert.Equal(OverlayStartResult.WrongProcess, OverlayRuntime.Start(elsewhere.ToBlob()));
        Assert.Null(OverlayRuntime.Options);
    }

    [Fact]
    public void RefusesAProtocolVersionItDoesNotImplement()
    {
        OverlayStartupOptions future = Valid() with { ProtocolVersion = OverlayProtocol.Version + 1 };

        Assert.Equal(OverlayStartResult.ProtocolMismatch, OverlayRuntime.Start(future.ToBlob()));
        Assert.Null(OverlayRuntime.Options);
    }

    [Fact]
    public void RefusesABlobTooSmallToBeOne()
    {
        // The struct is fixed-size, so "malformed" now means "too short to be the blob at all" - a
        // truncated or empty remote write. Both undo the start latch, so a corrected retry still works.
        Assert.Equal(OverlayStartResult.BadArgument, OverlayRuntime.Start(ReadOnlySpan<byte>.Empty));
        Assert.Equal(OverlayStartResult.BadArgument, OverlayRuntime.Start(new byte[OverlayStartupOptions.BlobSize - 1]));
    }

    [Fact]
    public void ARefusalDoesNotLatch()
    {
        // A shim that refused once has to accept a corrected injection, or a mis-aimed first attempt
        // would mean restarting the game.
        Assert.Equal(OverlayStartResult.WrongProcess, OverlayRuntime.Start((Valid() with { TargetProcess = "nope.exe" }).ToBlob()));
        Assert.Equal(OverlayStartResult.Success, OverlayRuntime.Start(Valid().ToBlob()));
    }

    [Fact]
    public void ASecondStartIsANoOp()
    {
        Assert.Equal(OverlayStartResult.Success, OverlayRuntime.Start(Valid().ToBlob()));
        Assert.Equal(OverlayStartResult.AlreadyRunning, OverlayRuntime.Start(Valid().ToBlob()));
    }

    [Fact]
    public void StopThenStartWorksAgain()
    {
        Assert.Equal(OverlayStartResult.Success, OverlayRuntime.Start(Valid().ToBlob()));
        OverlayRuntime.Stop();
        Assert.Null(OverlayRuntime.Options);
        Assert.Equal(OverlayStartResult.Success, OverlayRuntime.Start(Valid().ToBlob()));
    }

    [Fact]
    public void AnOwnerThatIsAlreadyGoneDoesNotStopTheOverlayStarting()
    {
        // The runner could exit between injecting and the shim reading its blob. Refusing to start
        // over that would be worse than running unwatched, so the shim says so in the log and carries
        // on. int.MaxValue is not a live pid.
        OverlayStartupOptions orphan = Valid() with { OwnerProcessId = int.MaxValue };

        Assert.Equal(OverlayStartResult.Success, OverlayRuntime.Start(orphan.ToBlob()));
    }

    [Fact]
    public async Task TheOverlayDetachesWhenItsOwnerExits()
    {
        // The case an orderly shutdown cannot cover: the runner is killed rather than asked to stop.
        // Once Present is hooked, an orphaned shim is a detour in a game with nothing left alive that
        // could remove it, so this is the safety net rather than a nicety.
        using Process owner = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            Assert.Equal(
                OverlayStartResult.Success,
                OverlayRuntime.Start((Valid() with { OwnerProcessId = owner.Id }).ToBlob()));

            Assert.NotNull(OverlayRuntime.Options);

            owner.Kill();
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);

            // The watchdog is blocked on the process handle, so it wakes as soon as the kernel
            // signals it - but "as soon as" still means scheduling, hence a bounded wait rather than
            // an immediate assert.
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (OverlayRuntime.Options is not null && !timeout.IsCancellationRequested)
                await Task.Delay(25, TestContext.Current.CancellationToken);

            Assert.Null(OverlayRuntime.Options);
        }
        finally
        {
            if (!owner.HasExited)
                owner.Kill();
        }
    }

    [Fact]
    public void TheBlobRoundTrips()
    {
        OverlayStartupOptions original = Valid() with { PipeName = "SRTHost.abc.overlay", LogPath = @"C:\temp\o.log" };
        OverlayStartupOptions? restored = OverlayStartupOptions.FromBlob(original.ToBlob());

        Assert.Equal(original, restored);
    }

    [Fact]
    public void BrightnessAndForcePqRoundTrip()
    {
        OverlayStartupOptions original = Valid() with { OverlayBrightness = 2.5f, OverlayForcePq = true };
        OverlayStartupOptions? restored = OverlayStartupOptions.FromBlob(original.ToBlob());

        Assert.NotNull(restored);
        Assert.Equal(2.5f, restored!.OverlayBrightness);
        Assert.True(restored.OverlayForcePq);
    }

    [Fact]
    public void AbsentBrightnessAndForcePqReadBackAsNull()
    {
        // Pins the tri-state encoding: null (and, for brightness, a non-positive value) travels as the
        // "use the per-format default" case and comes back null, not as 0 or false. The composite's
        // ChooseColorAdjust depends on null meaning "leave the default".
        OverlayStartupOptions? restored = OverlayStartupOptions.FromBlob(Valid().ToBlob());

        Assert.NotNull(restored);
        Assert.Null(restored!.OverlayBrightness);
        Assert.Null(restored.OverlayForcePq);
    }
}
