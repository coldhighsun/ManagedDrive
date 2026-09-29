namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how <see cref="DisposeProgressAggregator"/> combines per-disk save progress.
/// </summary>
public sealed class DisposeProgressAggregatorTests
{
    /// <summary>
    /// The overall fraction is the average of every disk's progress.
    /// </summary>
    [Fact]
    public void Report_SeveralDisks_ReturnsAverageAsOverall()
    {
        var aggregator = new DisposeProgressAggregator(2);

        aggregator.Report(0, 1.0);
        var (disk, overall) = aggregator.Report(1, 0.5);

        Assert.Equal(0.5, disk);
        Assert.Equal(0.75, overall);
    }

    /// <summary>
    /// A report arriving after a later one (e.g. a mid-save tick after the final 1.0) doesn't
    /// move that disk's progress, or the overall fraction, backwards.
    /// </summary>
    [Fact]
    public void Report_LowerThanEarlierReport_KeepsEarlierProgress()
    {
        var aggregator = new DisposeProgressAggregator(2);
        aggregator.Report(0, 1.0);

        var (disk, overall) = aggregator.Report(0, 0.3);

        Assert.Equal(1.0, disk);
        Assert.Equal(0.5, overall);
    }

    /// <summary>
    /// Values outside [0, 1] are clamped into it.
    /// </summary>
    /// <param name="reported">The out-of-range value reported.</param>
    /// <param name="expected">The clamped progress.</param>
    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    public void Report_OutOfRange_ClampsToUnitInterval(double reported, double expected)
    {
        var aggregator = new DisposeProgressAggregator(1);

        var (disk, overall) = aggregator.Report(0, reported);

        Assert.Equal(expected, disk);
        Assert.Equal(expected, overall);
    }
}
