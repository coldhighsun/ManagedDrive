using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SplashDragTracker"/>, the pure bookkeeping behind dragging the splash.
/// </summary>
public sealed class SplashDragTrackerTests
{
    /// <summary>
    /// Without a drag in progress nothing moves.
    /// </summary>
    [Fact]
    public void Move_BeforeBegin_ReturnsNull()
    {
        var tracker = new SplashDragTracker();

        var result = tracker.Move(100, 100);

        Assert.Null(result);
        Assert.False(tracker.IsDragging);
    }

    /// <summary>
    /// The window follows the cursor's delta.
    /// </summary>
    [Fact]
    public void Move_AfterBegin_AppliesCursorDelta()
    {
        var tracker = new SplashDragTracker();
        tracker.Begin(500, 400, 100, 50);

        var result = tracker.Move(530, 380);

        Assert.Equal(130, result!.Value.Left);
        Assert.Equal(30, result.Value.Top);
        Assert.True(tracker.IsDragging);
    }

    /// <summary>
    /// After the drag ends, moves are ignored.
    /// </summary>
    [Fact]
    public void Move_AfterEnd_ReturnsNull()
    {
        var tracker = new SplashDragTracker();
        tracker.Begin(0, 0, 0, 0);
        tracker.End();

        var result = tracker.Move(10, 10);

        Assert.Null(result);
        Assert.False(tracker.IsDragging);
    }

    /// <summary>
    /// A second Begin (re-anchoring after a DPI change) takes the new origin, not the old one.
    /// </summary>
    [Fact]
    public void Begin_Twice_UsesLatestOrigin()
    {
        var tracker = new SplashDragTracker();
        tracker.Begin(0, 0, 0, 0);
        tracker.Begin(100, 100, 50, 50);

        var result = tracker.Move(110, 90);

        Assert.Equal(60, result!.Value.Left);
        Assert.Equal(40, result.Value.Top);
    }
}
