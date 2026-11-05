using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="BalloonClickTargetTracker"/>.
/// </summary>
public sealed class BalloonClickTargetTrackerTests
{
    /// <summary>
    /// A click gets the target of the balloon shown.
    /// </summary>
    [Fact]
    public void TakeForClick_BalloonShown_ReturnsItsTarget()
    {
        var tracker = new BalloonClickTargetTracker();
        tracker.OnBalloonShowing(NotificationClickTarget.Update);

        var target = tracker.TakeForClick();

        Assert.Equal(NotificationClickTarget.Update, target);
    }

    /// <summary>
    /// A later balloon replaces the earlier one's target.
    /// </summary>
    [Fact]
    public void TakeForClick_BalloonReplaced_ReturnsLatestTarget()
    {
        var tracker = new BalloonClickTargetTracker();
        tracker.OnBalloonShowing(NotificationClickTarget.EditDisk("R:"));
        tracker.OnBalloonShowing(NotificationClickTarget.Disk("S:"));

        var target = tracker.TakeForClick();

        Assert.Equal(NotificationClickTarget.Disk("S:"), target);
    }

    /// <summary>
    /// A balloon that only informs must not run the action of the balloon before it.
    /// </summary>
    [Fact]
    public void TakeForClick_ReplacedByBalloonWithoutTarget_ReturnsNull()
    {
        var tracker = new BalloonClickTargetTracker();
        tracker.OnBalloonShowing(NotificationClickTarget.Update);
        tracker.OnBalloonShowing(null);

        var target = tracker.TakeForClick();

        Assert.Null(target);
    }

    /// <summary>
    /// A target runs once: a second click finds nothing.
    /// </summary>
    [Fact]
    public void TakeForClick_SecondClick_ReturnsNull()
    {
        var tracker = new BalloonClickTargetTracker();
        tracker.OnBalloonShowing(NotificationClickTarget.ShowWindow);
        tracker.TakeForClick();

        var target = tracker.TakeForClick();

        Assert.Null(target);
    }
}
