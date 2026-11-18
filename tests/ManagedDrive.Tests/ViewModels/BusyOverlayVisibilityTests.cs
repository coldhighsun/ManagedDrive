using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests when the busy overlay is shown and hidden, so a quick operation does not make it flash.
/// </summary>
public sealed class BusyOverlayVisibilityTests
{
    /// <summary>
    /// Hands out delays the test finishes by hand, and a clock the test moves by hand.
    /// </summary>
    private sealed class Harness
    {
        /// <summary>
        /// The delays asked for so far, with the source that ends each one.
        /// </summary>
        public List<(TimeSpan Duration, TaskCompletionSource Source)> Delays { get; } = [];

        /// <summary>
        /// The clock the overlay measures its visible time with.
        /// </summary>
        public ManualTimeProvider Clock { get; } = new();

        /// <summary>
        /// Gets the overlay under test, with the 300 ms show delay and 500 ms minimum of the policy.
        /// </summary>
        public BusyOverlayViewModel Overlay { get; }

        /// <summary>
        /// Creates the overlay with delays that wait for <see cref="Finish"/>.
        /// </summary>
        public Harness() => Overlay = new(
            BusyOverlayPolicy.ShowDelay,
            BusyOverlayPolicy.MinimumVisible,
            duration =>
            {
                var source = new TaskCompletionSource();
                Delays.Add((duration, source));
                return source.Task;
            },
            Clock);

        /// <summary>
        /// Ends the delay with the given index. The source runs continuations synchronously, so the
        /// overlay has reacted when this returns.
        /// </summary>
        /// <param name="index">Index in <see cref="Delays"/>.</param>
        public void Finish(int index) => Delays[index].Source.SetResult();
    }

    /// <summary>
    /// A clock that only moves when told to.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        /// <summary>
        /// The current timestamp, in ticks of <see cref="TimestampFrequency"/>.
        /// </summary>
        private long _timestamp;

        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <inheritdoc />
        public override long GetTimestamp() => _timestamp;

        /// <summary>
        /// Moves the clock forward.
        /// </summary>
        /// <param name="by">How far.</param>
        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }

    /// <summary>
    /// How much longer the overlay must stay up is the minimum minus the time it has been shown, never negative.
    /// </summary>
    [Theory]
    [InlineData(0, 500, 500)]
    [InlineData(200, 500, 300)]
    [InlineData(500, 500, 0)]
    [InlineData(800, 500, 0)]
    public void RemainingVisible_ForTimeShown_ReturnsWhatIsLeftOfTheMinimum(int shownMs, int minimumMs, int expectedMs)
    {
        var remaining = BusyOverlayPolicy.RemainingVisible(
            TimeSpan.FromMilliseconds(shownMs), TimeSpan.FromMilliseconds(minimumMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), remaining);
    }

    /// <summary>
    /// Right after an operation starts it is busy and input is blocked, but nothing is drawn yet.
    /// </summary>
    [Fact]
    public void Start_Immediately_IsBusyButOverlayNotYetVisible()
    {
        var harness = new Harness();

        harness.Overlay.Start("Working...");

        Assert.True(harness.Overlay.IsBusy);
        Assert.False(harness.Overlay.IsOverlayVisible);
        Assert.True(harness.Overlay.IsInputBlocked);
    }

    /// <summary>
    /// An operation that finishes before the show delay never shows the overlay, even when the delay ends later.
    /// </summary>
    [Fact]
    public void Stop_BeforeShowDelayEnds_NeverShowsTheOverlay()
    {
        var harness = new Harness();
        harness.Overlay.Start("Working...");

        harness.Overlay.Stop();
        harness.Finish(0);

        Assert.False(harness.Overlay.IsOverlayVisible);
        Assert.False(harness.Overlay.IsInputBlocked);
    }

    /// <summary>
    /// Once the operation has run for the show delay, the overlay appears.
    /// </summary>
    [Fact]
    public void Start_AfterShowDelay_ShowsTheOverlay()
    {
        var harness = new Harness();
        harness.Overlay.Start("Working...");

        harness.Finish(0);

        Assert.Equal(BusyOverlayPolicy.ShowDelay, harness.Delays[0].Duration);
        Assert.True(harness.Overlay.IsOverlayVisible);
    }

    /// <summary>
    /// An operation that finishes soon after the overlay appeared keeps it up for the rest of the minimum time.
    /// </summary>
    [Fact]
    public void Stop_ShortlyAfterTheOverlayAppeared_KeepsItUpUntilTheMinimumHasPassed()
    {
        var harness = new Harness();
        harness.Overlay.Start("Working...");
        harness.Finish(0);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(100));

        harness.Overlay.Stop();

        Assert.False(harness.Overlay.IsBusy);
        Assert.True(harness.Overlay.IsOverlayVisible);
        Assert.Equal(TimeSpan.FromMilliseconds(400), harness.Delays[1].Duration);

        harness.Finish(1);

        Assert.False(harness.Overlay.IsOverlayVisible);
    }

    /// <summary>
    /// An operation that finishes after the minimum visible time hides the overlay at once.
    /// </summary>
    [Fact]
    public void Stop_AfterTheMinimumVisibleTime_HidesTheOverlayAtOnce()
    {
        var harness = new Harness();
        harness.Overlay.Start("Working...");
        harness.Finish(0);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));

        harness.Overlay.Stop();

        Assert.False(harness.Overlay.IsOverlayVisible);
        Assert.Single(harness.Delays);
    }

    /// <summary>
    /// A new operation started while the overlay lingers keeps it up instead of letting the pending hide win.
    /// </summary>
    [Fact]
    public void Start_WhileTheOverlayIsStillUpFromThePreviousOperation_KeepsItUp()
    {
        var harness = new Harness();
        harness.Overlay.Start("First...");
        harness.Finish(0);
        harness.Overlay.Stop();

        harness.Overlay.Start("Second...");
        harness.Finish(1);

        Assert.True(harness.Overlay.IsBusy);
        Assert.True(harness.Overlay.IsOverlayVisible);
    }

    /// <summary>
    /// The show delay of a quick, finished operation does not show the overlay for the next one early.
    /// </summary>
    [Fact]
    public void Start_AfterAQuickOperation_IsNotHiddenByTheEarlierDelay()
    {
        var harness = new Harness();
        harness.Overlay.Start("First...");
        harness.Overlay.Stop();
        harness.Overlay.Start("Second...");

        harness.Finish(0);
        Assert.False(harness.Overlay.IsOverlayVisible);

        harness.Finish(1);
        Assert.True(harness.Overlay.IsOverlayVisible);
    }

    /// <summary>
    /// An operation started with showImmediately draws the overlay without waiting for the show delay.
    /// </summary>
    [Fact]
    public void Start_WithShowImmediately_ShowsTheOverlayAtOnce()
    {
        var harness = new Harness();

        harness.Overlay.Start("Loading...", showImmediately: true);

        Assert.True(harness.Overlay.IsOverlayVisible);
        Assert.Empty(harness.Delays);
    }

    /// <summary>
    /// With no delays configured the overlay follows the operation exactly.
    /// </summary>
    [Fact]
    public void Start_WithZeroShowDelay_ShowsTheOverlayAtOnce()
    {
        var overlay = new BusyOverlayViewModel(showDelay: TimeSpan.Zero, minimumVisible: TimeSpan.Zero);

        overlay.Start("Working...");
        Assert.True(overlay.IsOverlayVisible);

        overlay.Stop();
        Assert.False(overlay.IsOverlayVisible);
    }
}
