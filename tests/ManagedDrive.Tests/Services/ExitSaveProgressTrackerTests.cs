using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="ExitSaveProgressTracker"/>, which keeps the exit overlay stable while
/// several disks report progress in parallel.
/// </summary>
public sealed class ExitSaveProgressTrackerTests
{
    /// <summary>
    /// Creates a tracker for disks with the given sizes.
    /// </summary>
    /// <param name="disks">Mount point and used bytes of each disk.</param>
    /// <returns>The tracker.</returns>
    private static ExitSaveProgressTracker Create(params (string MountPoint, ulong Bytes)[] disks) =>
        new(disks.ToDictionary(d => d.MountPoint, d => d.Bytes));

    /// <summary>
    /// Reports a disk's progress and returns the resulting state.
    /// </summary>
    private static ExitSaveProgressTracker.Snapshot Report(ExitSaveProgressTracker tracker, string mountPoint, double fraction)
    {
        tracker.Report(mountPoint, fraction);
        return tracker.Sample();
    }

    /// <summary>
    /// A single disk keeps its own name in the status text.
    /// </summary>
    [Fact]
    public void SingleDisk_ReportsItsMountPoint()
    {
        var tracker = Create(("R:", 1000));

        var s = Report(tracker, "R:", 0.5);

        Assert.Equal("R:", s.MountPoint);
        Assert.Equal(500UL, s.DoneBytes);
    }

    /// <summary>
    /// With several disks no single mount point is shown, however the reports interleave.
    /// </summary>
    [Fact]
    public void MultipleDisks_DoNotNameOneDisk()
    {
        var tracker = Create(("R:", 1000), ("S:", 4000));

        Assert.Null(Report(tracker, "R:", 0.5).MountPoint);
        Assert.Null(Report(tracker, "S:", 0.5).MountPoint);
        Assert.Null(Report(tracker, "R:", 0.6).MountPoint);
    }

    /// <summary>
    /// The byte total covers every disk from the first report, including disks not started yet.
    /// </summary>
    [Fact]
    public void TotalBytes_IsFixedFromTheFirstReport()
    {
        var tracker = Create(("R:", 1000), ("S:", 4000), ("T:", 5000));

        var first = Report(tracker, "R:", 0.5);
        var later = Report(tracker, "S:", 0.5);

        Assert.Equal(10000UL, first.TotalBytes);
        Assert.Equal(10000UL, later.TotalBytes);
    }

    /// <summary>
    /// Overall progress is weighted by size, so a small finished disk barely moves it.
    /// </summary>
    [Fact]
    public void Overall_IsWeightedBySize()
    {
        var tracker = Create(("R:", 10), ("S:", 990));

        var s = Report(tracker, "R:", 1.0);

        Assert.Equal(0.01, s.Overall, 3);
    }

    /// <summary>
    /// Overall progress and bytes written never go back when reports arrive out of order.
    /// </summary>
    [Fact]
    public void OutOfOrderReports_NeverMoveBackwards()
    {
        var tracker = Create(("R:", 1000), ("S:", 1000));
        var first = Report(tracker, "R:", 0.8);

        var late = Report(tracker, "R:", 0.4);

        Assert.Equal(first.Overall, late.Overall);
        Assert.Equal(first.DoneBytes, late.DoneBytes);
    }

    /// <summary>
    /// Finished disks are counted.
    /// </summary>
    [Fact]
    public void CompletedDisks_AreCounted()
    {
        var tracker = Create(("R:", 100), ("S:", 100), ("T:", 100));
        tracker.Report("R:", 1.0);
        tracker.Report("S:", 0.5);

        var s = Report(tracker, "T:", 1.0);

        Assert.Equal(2, s.CompletedCount);
        Assert.Equal(3, s.DiskCount);
    }

    /// <summary>
    /// Disks with nothing to write count equally towards overall progress.
    /// </summary>
    [Fact]
    public void EmptyDisks_CountEquallyTowardsOverall()
    {
        var tracker = Create(("R:", 0), ("S:", 0));

        var s = Report(tracker, "R:", 1.0);

        Assert.Equal(0.5, s.Overall, 3);
    }

    /// <summary>
    /// Completing fills the bar even when the disks have no bytes to weigh.
    /// </summary>
    [Fact]
    public void EmptyDisks_CompleteFillsTheBar()
    {
        var tracker = Create(("R:", 0), ("S:", 0));

        tracker.Complete();

        Assert.Equal(1.0, tracker.Sample().Overall);
    }

    /// <summary>
    /// Once every disk is done but the save is not completed the state is 'finishing', never with a failure.
    /// </summary>
    [Fact]
    public void AllDisksDone_IsFinishingUnlessOneFailed()
    {
        var tracker = Create(("R:", 100), ("S:", 100));
        Assert.False(Report(tracker, "R:", 1.0).IsFinishing);
        Assert.True(Report(tracker, "S:", 1.0).IsFinishing);

        var failed = Create(("R:", 100), ("S:", 100));
        failed.Report("R:", 0.5);
        failed.MarkFailed("R:");
        failed.Report("S:", 1.0);
        Assert.False(failed.Sample().IsFinishing);
    }

    /// <summary>
    /// A disk that is not being tracked is ignored and leaves the totals alone.
    /// </summary>
    [Fact]
    public void UnknownDisk_IsIgnored()
    {
        var tracker = Create(("R:", 1000));

        var s = Report(tracker, "S:", 1.0);

        Assert.Equal(1000UL, s.TotalBytes);
        Assert.Equal(0UL, s.DoneBytes);
        Assert.Equal(1, s.DiskCount);
    }

    /// <summary>
    /// Completing marks every disk finished even if it never reported 1.
    /// </summary>
    [Fact]
    public void Complete_MarksEveryDiskFinished()
    {
        var tracker = Create(("R:", 1000), ("S:", 3000));
        tracker.Report("R:", 0.5);

        tracker.Complete();
        var s = tracker.Sample();

        Assert.Equal(1.0, s.Overall);
        Assert.Equal(2, s.CompletedCount);
        Assert.Equal(4000UL, s.DoneBytes);
    }

    /// <summary>
    /// The bar stays below full until completion, since an untracked disk may still be saving.
    /// </summary>
    [Fact]
    public void BeforeComplete_OverallStaysBelowFull()
    {
        var tracker = Create(("R:", 1000));

        var s = Report(tracker, "R:", 1.0);

        Assert.True(s.Overall < 1.0);
    }

    /// <summary>
    /// A disk whose save failed is not shown as finished, even if it reports 1 afterwards or the
    /// rest is completed.
    /// </summary>
    [Fact]
    public void FailedDisk_IsNeverShownAsFinished()
    {
        var tracker = Create(("R:", 1000), ("S:", 1000));
        tracker.Report("R:", 0.4);
        tracker.MarkFailed("R:");
        tracker.Report("R:", 1.0);
        tracker.Report("S:", 1.0);

        tracker.Complete();
        var s = tracker.Sample();

        Assert.Equal(1, s.CompletedCount);
        Assert.True(s.Overall < 1.0);
    }

    /// <summary>
    /// Mount points are matched without regard to case, and two spellings of one disk collapse.
    /// </summary>
    [Fact]
    public void MountPoints_AreMatchedIgnoringCase()
    {
        var tracker = new ExitSaveProgressTracker(new Dictionary<string, ulong> { ["R:"] = 1000, ["r:"] = 1000 });

        var s = Report(tracker, "r:", 0.5);

        Assert.Equal(1, s.DiskCount);
        Assert.Equal(500UL, s.DoneBytes);
    }

    /// <summary>
    /// Reports racing in from several threads never lose a value or throw.
    /// </summary>
    [Fact]
    public void ConcurrentReports_EndAtTheFinalState()
    {
        var disks = Enumerable.Range(0, 8).Select(i => ((string)$"D{i}:", 1000UL)).ToArray();
        var tracker = Create(disks);

        Parallel.ForEach(disks, d =>
        {
            for (var step = 0; step <= 100; step++)
            {
                tracker.Report(d.Item1, step / 100.0);
                tracker.Sample();
            }
        });
        Assert.Equal(8, tracker.Sample().CompletedCount);
        tracker.Complete();

        Assert.Equal(8000UL, tracker.Sample().DoneBytes);
    }

    /// <summary>
    /// Out-of-range fractions are clamped into [0, 1], so bytes never exceed the total (nor the
    /// 99% ceiling that applies until completion).
    /// </summary>
    /// <param name="reported">The out-of-range fraction reported.</param>
    /// <param name="expectedBytes">The bytes it counts as.</param>
    [Theory]
    [InlineData(-0.5, 0UL)]
    [InlineData(1.5, 990UL)]
    public void OutOfRangeFraction_IsClamped(double reported, ulong expectedBytes)
    {
        var tracker = Create(("R:", 1000));

        var s = Report(tracker, "R:", reported);

        Assert.Equal(expectedBytes, s.DoneBytes);
    }

    /// <summary>
    /// A report only asks for a refresh when it changes something: the first report of a disk or a
    /// higher fraction does; an unknown disk, a repeat, a lower fraction or a failed disk does not.
    /// </summary>
    [Fact]
    public void Report_ReturnsWhetherAnythingChanged()
    {
        var tracker = Create(("R:", 1000), ("S:", 1000));

        Assert.True(tracker.Report("R:", 0.0));
        Assert.False(tracker.Report("R:", 0.0));
        Assert.True(tracker.Report("R:", 0.5));
        Assert.False(tracker.Report("R:", 0.3));
        Assert.False(tracker.Report("X:", 0.5));
        tracker.Report("S:", 0.1);
        tracker.MarkFailed("S:");
        Assert.False(tracker.Report("S:", 1.0));
    }

    /// <summary>
    /// A tiny finished disk does not read as unstarted because of the ceiling's rounding.
    /// </summary>
    [Fact]
    public void TinyDisk_WhenDone_ShowsItsBytes()
    {
        var tracker = Create(("R:", 1));

        var s = Report(tracker, "R:", 1.0);

        Assert.Equal(1UL, s.DoneBytes);
    }

    /// <summary>
    /// The byte count shares the ceiling with the bar until completion, so they cannot disagree.
    /// </summary>
    [Fact]
    public void BeforeComplete_BytesStayBelowTheTotalLikeTheBar()
    {
        var tracker = Create(("R:", 1000));

        var s = Report(tracker, "R:", 1.0);

        Assert.True(s.DoneBytes < s.TotalBytes);
        Assert.Equal(s.Overall, (double)s.DoneBytes / s.TotalBytes, 3);
    }

    /// <summary>
    /// A failure reported before the disk's own save has begun (an auto-save) is ignored.
    /// </summary>
    [Fact]
    public void FailureBeforeTheDiskStarted_IsIgnored()
    {
        var tracker = Create(("R:", 1000));
        tracker.MarkFailed("R:");

        Report(tracker, "R:", 1.0);
        tracker.Complete();
        var s = tracker.Sample();

        Assert.Equal(0, s.FailedCount);
        Assert.Equal(1, s.CompletedCount);
    }

    /// <summary>
    /// Once every disk is finished or failed, with a failure, the save has ended with failures.
    /// </summary>
    [Fact]
    public void EndedWithFailures_WhenEveryDiskIsFinishedOrFailed()
    {
        var tracker = Create(("R:", 100), ("S:", 100));
        tracker.Report("R:", 0.3);
        tracker.MarkFailed("R:");
        Assert.False(tracker.Sample().EndedWithFailures);

        var s = Report(tracker, "S:", 1.0);

        Assert.True(s.EndedWithFailures);
        Assert.Equal(1, s.FailedCount);
    }
}
