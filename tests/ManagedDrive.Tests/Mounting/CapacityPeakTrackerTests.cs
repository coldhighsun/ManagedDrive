namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="CapacityPeakTracker"/>.
/// </summary>
public sealed class CapacityPeakTrackerTests
{
    /// <summary>
    /// A new watch's peak is the capacity in effect when it was opened.
    /// </summary>
    [Fact]
    public void Start_NoRaise_PeakIsCurrentCapacity()
    {
        var tracker = new CapacityPeakTracker();

        using var watch = tracker.Start(() => 100);

        Assert.Equal(100UL, watch.Peak);
    }

    /// <summary>
    /// A capacity grown while the watch is open raises its peak, and a later shrink does not lower it.
    /// </summary>
    [Fact]
    public void Raise_GrowThenShrink_KeepsLargestCapacity()
    {
        var tracker = new CapacityPeakTracker();
        using var watch = tracker.Start(() => 100);

        tracker.Raise(300);
        tracker.Raise(50);

        Assert.Equal(300UL, watch.Peak);
    }

    /// <summary>
    /// A closed watch no longer follows the capacity; open ones still do.
    /// </summary>
    [Fact]
    public void Raise_AfterWatchDisposed_LeavesClosedWatchAlone()
    {
        var tracker = new CapacityPeakTracker();
        var closed = tracker.Start(() => 100);
        using var open = tracker.Start(() => 100);
        closed.Dispose();

        tracker.Raise(500);

        Assert.Equal(100UL, closed.Peak);
        Assert.Equal(500UL, open.Peak);
    }

    /// <summary>
    /// Every open watch follows the capacity on its own, from the value it was opened with.
    /// </summary>
    [Fact]
    public void Raise_SeveralWatches_EachKeepsOwnPeak()
    {
        var tracker = new CapacityPeakTracker();
        using var first = tracker.Start(() => 100);
        tracker.Raise(200);
        using var second = tracker.Start(() => 400);

        tracker.Raise(300);

        Assert.Equal(300UL, first.Peak);
        Assert.Equal(400UL, second.Peak);
    }
}
