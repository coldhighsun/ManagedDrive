using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="BalloonOnlyIconTracker"/>.
/// </summary>
public sealed class BalloonOnlyIconTrackerTests
{
    /// <summary>
    /// A hidden icon is shown for a balloon and hidden again when that balloon closes.
    /// </summary>
    [Fact]
    public void OnBalloonClosed_OnlyBalloonCloses_HidesIcon()
    {
        var tracker = new BalloonOnlyIconTracker();
        var mustShow = tracker.OnBalloonShowing(iconVisible: false);

        var mustHide = tracker.OnBalloonClosed();

        Assert.True(mustShow);
        Assert.True(mustHide);
        Assert.False(tracker.Reset());
    }

    /// <summary>
    /// When a second balloon replaces the first, the first one's close must not hide the icon
    /// (which would dismiss the second balloon too); the second one's close does.
    /// </summary>
    [Fact]
    public void OnBalloonClosed_NewerBalloonStillOpen_KeepsIconUntilItCloses()
    {
        var tracker = new BalloonOnlyIconTracker();
        tracker.OnBalloonShowing(iconVisible: false);
        var mustShowAgain = tracker.OnBalloonShowing(iconVisible: true);

        var hideOnFirstClose = tracker.OnBalloonClosed();
        var hideOnSecondClose = tracker.OnBalloonClosed();

        Assert.False(mustShowAgain);
        Assert.False(hideOnFirstClose);
        Assert.True(hideOnSecondClose);
    }

    /// <summary>
    /// An icon that was already visible (window hidden to the tray) is never hidden by a
    /// balloon closing.
    /// </summary>
    [Fact]
    public void OnBalloonClosed_IconAlreadyVisible_DoesNotHideIcon()
    {
        var tracker = new BalloonOnlyIconTracker();
        var mustShow = tracker.OnBalloonShowing(iconVisible: true);

        var mustHide = tracker.OnBalloonClosed();

        Assert.False(mustShow);
        Assert.False(mustHide);
    }

    /// <summary>
    /// Resetting reports whether the icon was only shown for balloons, and a close notification
    /// arriving afterwards no longer hides the icon.
    /// </summary>
    [Fact]
    public void Reset_WhileShownForBalloons_ReportsItAndStopsTracking()
    {
        var tracker = new BalloonOnlyIconTracker();
        tracker.OnBalloonShowing(iconVisible: false);

        var wasShownForBalloons = tracker.Reset();
        var mustHideOnLateClose = tracker.OnBalloonClosed();

        Assert.True(wasShownForBalloons);
        Assert.False(mustHideOnLateClose);
        Assert.False(tracker.Reset());
    }
}
