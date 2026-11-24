namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="ProgressThrottle"/>.
/// </summary>
public sealed class ProgressThrottleTests
{
    /// <summary>
    /// Reporting to no reporter is a no-op.
    /// </summary>
    [Fact]
    public void Report_NullProgress_DoesNotThrow()
    {
        var throttle = new ProgressThrottle();

        throttle.Report(null, 0.5);
    }

    /// <summary>
    /// Tens of thousands of tiny steps produce at most a few hundred reports.
    /// </summary>
    [Fact]
    public void Report_ManySmallSteps_ReportsFarFewerTimesThanCalls()
    {
        var reported = new List<double>();
        var progress = new DelegateProgress(reported.Add);
        var throttle = new ProgressThrottle();
        const int count = 50_000;

        for (var i = 1; i <= count; i++)
        {
            throttle.Report(progress, (double)i / count);
        }

        Assert.InRange(reported.Count, 1, 250);
    }

    /// <summary>
    /// The last report is exactly 1.0.
    /// </summary>
    [Fact]
    public void Report_ManySmallSteps_ReportsFinalValueOfOne()
    {
        var reported = new List<double>();
        var progress = new DelegateProgress(reported.Add);
        var throttle = new ProgressThrottle();
        const int count = 10_000;

        for (var i = 1; i <= count; i++)
        {
            throttle.Report(progress, (double)i / count);
        }

        Assert.Equal(1.0, reported[^1]);
    }

    /// <summary>
    /// Reported values never go backwards.
    /// </summary>
    [Fact]
    public void Report_ManySmallSteps_ReportsNonDecreasingValues()
    {
        var reported = new List<double>();
        var progress = new DelegateProgress(reported.Add);
        var throttle = new ProgressThrottle();

        for (var i = 1; i <= 5_000; i++)
        {
            throttle.Report(progress, i / 5_000.0);
        }

        Assert.Equal(reported.OrderBy(v => v), reported);
    }

    /// <summary>
    /// The first value always goes through, however small.
    /// </summary>
    [Fact]
    public void Report_FirstSmallValue_IsReported()
    {
        var reported = new List<double>();
        var progress = new DelegateProgress(reported.Add);
        var throttle = new ProgressThrottle();

        throttle.Report(progress, 0.0001);

        Assert.Equal([0.0001], reported);
    }

    /// <summary>
    /// A cancelled operation stops even when the step is too small to report.
    /// </summary>
    [Fact]
    public void Report_CancelledAndStepTooSmallToReport_StillThrows()
    {
        using var cts = new CancellationTokenSource();
        var reported = new List<double>();
        var progress = CancellableProgress.Wrap(new DelegateProgress(reported.Add), cts.Token)!;
        var throttle = new ProgressThrottle();
        throttle.Report(progress, 0.5);
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => throttle.Report(progress, 0.5001));
        Assert.Equal([0.5], reported);
    }

    /// <summary>
    /// A progress reporter that forwards every value to a delegate.
    /// </summary>
    /// <param name="onReport">Receives each reported value.</param>
    private sealed class DelegateProgress(Action<double> onReport) : IProgress<double>
    {
        /// <inheritdoc />
        public void Report(double value) => onReport(value);
    }
}
