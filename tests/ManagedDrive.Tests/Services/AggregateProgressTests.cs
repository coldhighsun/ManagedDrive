using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="AggregateProgress"/>, which merges the progress of disks loaded side by side
/// into the single startup progress bar.
/// </summary>
public sealed class AggregateProgressTests
{
    /// <summary>
    /// Operations count in proportion to their weight.
    /// </summary>
    [Fact]
    public void ForOperation_WeightedOperations_ReportsWeightedFraction()
    {
        var reported = new List<double>();
        var aggregate = new AggregateProgress([300, 100], reported.Add);

        aggregate.ForOperation(0).Report(1.0);

        Assert.Equal(0.75, Assert.Single(reported), precision: 6);
    }

    /// <summary>
    /// A report that would make the bar move backwards is not passed on.
    /// </summary>
    [Fact]
    public void ForOperation_FractionDrops_DoesNotReportLowerValue()
    {
        var reported = new List<double>();
        var aggregate = new AggregateProgress([1, 1], reported.Add);
        var first = aggregate.ForOperation(0);
        first.Report(0.8);

        first.Report(0.2);

        Assert.Single(reported);
    }

    /// <summary>
    /// Operations of unknown size (weight zero) take the average share instead of none.
    /// </summary>
    [Fact]
    public void Constructor_UnknownWeight_UsesAverageOfKnownWeights()
    {
        var reported = new List<double>();
        var aggregate = new AggregateProgress([200, 0], reported.Add);

        aggregate.ForOperation(1).Report(1.0);

        Assert.Equal(0.5, Assert.Single(reported), precision: 6);
    }

    /// <summary>
    /// With no usable weight at all the operations count equally.
    /// </summary>
    [Fact]
    public void Constructor_NoKnownWeights_CountsOperationsEqually()
    {
        var reported = new List<double>();
        var aggregate = new AggregateProgress([0, 0, 0, 0], reported.Add);

        aggregate.ForOperation(2).Report(1.0);

        Assert.Equal(0.25, Assert.Single(reported), precision: 6);
    }

    /// <summary>
    /// Completing every operation, even ones that never reported, ends at exactly 1.
    /// </summary>
    [Fact]
    public void Complete_AllOperations_ReachesOne()
    {
        var reported = new List<double>();
        var aggregate = new AggregateProgress([5, 7, 11], reported.Add);

        aggregate.ForOperation(1).Report(0.5);
        aggregate.Complete(0);
        aggregate.Complete(1);
        aggregate.Complete(2);

        Assert.Equal(1.0, reported[^1], precision: 6);
    }

    /// <summary>
    /// Out-of-range values from a misbehaving source are clamped.
    /// </summary>
    [Fact]
    public void ForOperation_ValueOutOfRange_IsClamped()
    {
        var reported = new List<double>();
        var aggregate = new AggregateProgress([1], reported.Add);

        aggregate.ForOperation(0).Report(7.0);

        Assert.Equal(1.0, Assert.Single(reported), precision: 6);
    }

    /// <summary>
    /// An operation number outside the range given at construction is rejected.
    /// </summary>
    [Fact]
    public void ForOperation_IndexOutOfRange_Throws()
    {
        var aggregate = new AggregateProgress([1, 1], _ => { });

        Assert.Throws<ArgumentOutOfRangeException>(() => aggregate.ForOperation(2));
    }

    /// <summary>
    /// Reports from several threads at once never move the bar backwards and end at 1.
    /// </summary>
    [Fact]
    public async Task ForOperation_ConcurrentReports_AreMonotonicAndComplete()
    {
        var reported = new List<double>();
        var aggregate = new AggregateProgress([1, 1, 1, 1], reported.Add);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            var progress = aggregate.ForOperation(i);
            for (var step = 1; step <= 100; step++)
            {
                progress.Report(step / 100.0);
            }
        })));

        Assert.Equal(1.0, reported[^1], precision: 6);
        Assert.Equal(reported.Order(), reported);
    }
}
