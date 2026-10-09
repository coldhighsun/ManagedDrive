using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SplashLifecycle"/>, the ordering of closing the startup splash and showing the
/// main window.
/// </summary>
public sealed class SplashLifecycleTests
{
    /// <summary>
    /// Records what the lifecycle did, in order, and lets a test control the clock and the delay.
    /// </summary>
    private sealed class Harness
    {
        /// <summary>
        /// The lifecycle under test.
        /// </summary>
        public SplashLifecycle Lifecycle { get; }

        /// <summary>
        /// The calls made, in order: "delay", "close", "main".
        /// </summary>
        public List<string> Calls { get; } = [];

        /// <summary>
        /// The delays requested.
        /// </summary>
        public List<TimeSpan> Delays { get; } = [];

        /// <summary>
        /// When set, the delay does not finish until this task completes.
        /// </summary>
        public TaskCompletionSource? DelayGate { get; set; }

        /// <summary>
        /// When set, closing the splash throws it.
        /// </summary>
        public Exception? CloseFailure { get; set; }

        /// <summary>
        /// When set, showing the main window throws it.
        /// </summary>
        public Exception? ShowFailure { get; set; }

        /// <summary>
        /// How long the splash is reported to have been up.
        /// </summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>
        /// Initializes the harness.
        /// </summary>
        /// <param name="startMinimized">Whether the main window stays hidden.</param>
        public Harness(bool startMinimized)
        {
            Lifecycle = new(
                startMinimized,
                () => Elapsed,
                async delay =>
                {
                    Calls.Add("delay");
                    Delays.Add(delay);
                    if (DelayGate is { } gate)
                    {
                        await gate.Task;
                    }
                },
                () =>
                {
                    Calls.Add("main");
                    if (ShowFailure is { } failure)
                    {
                        throw failure;
                    }
                });
        }

        /// <summary>
        /// Registers a splash whose close is recorded.
        /// </summary>
        public void ShowSplash() => Lifecycle.Show(() =>
        {
            Calls.Add("close");
            if (CloseFailure is { } failure)
            {
                throw failure;
            }
        });
    }

    /// <summary>
    /// A freshly shown splash waits out the minimum time, is closed, and only then the main window
    /// is shown.
    /// </summary>
    [Fact]
    public async Task CloseAsync_SplashShown_WaitsClosesThenShowsMainWindow()
    {
        var harness = new Harness(startMinimized: false) { Elapsed = TimeSpan.FromMilliseconds(200) };
        harness.ShowSplash();

        await harness.Lifecycle.CloseAsync();

        Assert.Equal(["delay", "close", "main"], harness.Calls);
        Assert.Equal(SplashPolicy.MinimumDisplay - TimeSpan.FromMilliseconds(200), Assert.Single(harness.Delays));
        Assert.True(harness.Lifecycle.Closed.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A splash that has already been up long enough is closed without waiting.
    /// </summary>
    [Fact]
    public async Task CloseAsync_MinimumDisplayReached_DoesNotWait()
    {
        var harness = new Harness(startMinimized: false) { Elapsed = SplashPolicy.MinimumDisplay };
        harness.ShowSplash();

        await harness.Lifecycle.CloseAsync();

        Assert.Equal(["close", "main"], harness.Calls);
    }

    /// <summary>
    /// A minimized start closes the splash but leaves the main window hidden.
    /// </summary>
    [Fact]
    public async Task CloseAsync_StartMinimized_ClosesSplashWithoutShowingMainWindow()
    {
        var harness = new Harness(startMinimized: true) { Elapsed = SplashPolicy.MinimumDisplay };
        harness.ShowSplash();

        await harness.Lifecycle.CloseAsync();

        Assert.Equal(["close"], harness.Calls);
        Assert.True(harness.Lifecycle.Closed.IsCompletedSuccessfully);
    }

    /// <summary>
    /// While the minimum time is still being waited out, the splash is not closed and the main
    /// window is not shown yet, and <see cref="SplashLifecycle.Closed"/> is still pending.
    /// </summary>
    [Fact]
    public async Task CloseAsync_WhileWaiting_NothingClosedYet()
    {
        var harness = new Harness(startMinimized: false) { DelayGate = new() };
        harness.ShowSplash();

        var closing = harness.Lifecycle.CloseAsync();

        Assert.False(harness.Lifecycle.Closed.IsCompleted);
        Assert.Equal(["delay"], harness.Calls);

        harness.DelayGate.SetResult();
        await closing;

        Assert.Equal(["delay", "close", "main"], harness.Calls);
        Assert.True(harness.Lifecycle.Closed.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A second call while the first is still finishing neither closes the splash again nor
    /// completes <see cref="SplashLifecycle.Closed"/> early.
    /// </summary>
    [Fact]
    public async Task CloseAsync_CalledTwiceWhileWaiting_ClosesOnceAndCompletesAfterFirst()
    {
        var harness = new Harness(startMinimized: false) { DelayGate = new() };
        harness.ShowSplash();
        var first = harness.Lifecycle.CloseAsync();

        await harness.Lifecycle.CloseAsync();

        Assert.False(harness.Lifecycle.Closed.IsCompleted);

        harness.DelayGate.SetResult();
        await first;

        Assert.Single(harness.Calls, "close");
        Assert.Single(harness.Calls, "main");
    }

    /// <summary>
    /// Without a splash there is nothing to close or show, but waiters are released.
    /// </summary>
    [Fact]
    public async Task CloseAsync_NoSplash_OnlyCompletesClosed()
    {
        var harness = new Harness(startMinimized: false);
        Assert.False(harness.Lifecycle.Closed.IsCompleted);

        await harness.Lifecycle.CloseAsync();

        Assert.Empty(harness.Calls);
        Assert.True(harness.Lifecycle.Closed.IsCompletedSuccessfully);
    }

    /// <summary>
    /// If closing the splash throws, the error reaches the caller but waiters on
    /// <see cref="SplashLifecycle.Closed"/> are still released and the main window is not shown.
    /// </summary>
    [Fact]
    public async Task CloseAsync_CloseThrows_PropagatesButStillCompletesClosed()
    {
        var failure = new InvalidOperationException("close failed");
        var harness = new Harness(startMinimized: false) { Elapsed = SplashPolicy.MinimumDisplay, CloseFailure = failure };
        harness.ShowSplash();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Lifecycle.CloseAsync());

        Assert.Same(failure, thrown);
        Assert.Equal(["close"], harness.Calls);
        Assert.True(harness.Lifecycle.Closed.IsCompletedSuccessfully);
    }

    /// <summary>
    /// If showing the main window throws, the error reaches the caller and
    /// <see cref="SplashLifecycle.Closed"/> still completes.
    /// </summary>
    [Fact]
    public async Task CloseAsync_ShowMainWindowThrows_PropagatesButStillCompletesClosed()
    {
        var harness = new Harness(startMinimized: false)
        {
            Elapsed = SplashPolicy.MinimumDisplay,
            ShowFailure = new InvalidOperationException("show failed")
        };
        harness.ShowSplash();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Lifecycle.CloseAsync());

        Assert.Equal(["close", "main"], harness.Calls);
        Assert.True(harness.Lifecycle.Closed.IsCompletedSuccessfully);
    }

    /// <summary>
    /// After a failed close, a later call is not treated as one still in progress: it does nothing
    /// more.
    /// </summary>
    [Fact]
    public async Task CloseAsync_AfterFailedClose_DoesNothingMore()
    {
        var harness = new Harness(startMinimized: false)
        {
            Elapsed = SplashPolicy.MinimumDisplay,
            CloseFailure = new InvalidOperationException("close failed")
        };
        harness.ShowSplash();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Lifecycle.CloseAsync());

        await harness.Lifecycle.CloseAsync();

        Assert.Equal(["close"], harness.Calls);
    }
}
