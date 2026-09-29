namespace ManagedDrive.Tests;

public sealed class ThroughputTrackerTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sample_FirstCall_ReturnsZeroAndEstablishesBaseline()
    {
        var tracker = new ThroughputTracker();

        var rate = tracker.Sample(1000, BaseTime);

        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void Sample_SteadyGrowth_ReturnsExpectedBytesPerSecond()
    {
        var tracker = new ThroughputTracker();
        tracker.Sample(0, BaseTime);

        var rate = tracker.Sample(2000, BaseTime.AddSeconds(2));

        Assert.Equal(1000.0, rate);
    }

    [Fact]
    public void Sample_NoGrowth_ReturnsZero()
    {
        var tracker = new ThroughputTracker();
        tracker.Sample(1000, BaseTime);

        var rate = tracker.Sample(1000, BaseTime.AddSeconds(2));

        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void Sample_NonMonotonicDecrease_ReturnsZeroNotNegative()
    {
        var tracker = new ThroughputTracker();
        tracker.Sample(1000, BaseTime);

        var rate = tracker.Sample(500, BaseTime.AddSeconds(2));

        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void Sample_ZeroElapsedTime_ReturnsZeroNotInfinity()
    {
        var tracker = new ThroughputTracker();
        tracker.Sample(1000, BaseTime);

        var rate = tracker.Sample(2000, BaseTime);

        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void Reset_ClearsBaseline_NextSampleReturnsZero()
    {
        var tracker = new ThroughputTracker();
        tracker.Sample(1000, BaseTime);
        tracker.Reset();

        var rate = tracker.Sample(5000, BaseTime.AddSeconds(2));

        Assert.Equal(0.0, rate);
    }
}
