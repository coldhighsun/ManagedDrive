using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="CoalescingProgress"/>, which merges bursts of progress reports into one
/// delivery so they cannot flood the UI thread.
/// </summary>
public sealed class CoalescingProgressTests
{
    /// <summary>
    /// Posted deliveries that have not run yet, standing in for the dispatcher queue.
    /// </summary>
    private readonly Queue<Action> _queue = new();

    /// <summary>
    /// Values received by the callback.
    /// </summary>
    private readonly List<double> _delivered = [];

    /// <summary>
    /// Creates the reporter under test, wired to the fake queue.
    /// </summary>
    /// <returns>The reporter.</returns>
    private CoalescingProgress Create() => new(_delivered.Add, _queue.Enqueue);

    /// <summary>
    /// A burst of reports before the delivery runs schedules one delivery carrying the newest value.
    /// </summary>
    [Fact]
    public void Report_BurstBeforeDelivery_DeliversOnlyNewestValueOnce()
    {
        var progress = Create();

        progress.Report(0.1);
        progress.Report(0.2);
        progress.Report(0.3);
        Assert.Single(_queue);
        _queue.Dequeue()();

        Assert.Equal(0.3, Assert.Single(_delivered), precision: 6);
    }

    /// <summary>
    /// Nothing is delivered until the posted action runs.
    /// </summary>
    [Fact]
    public void Report_BeforeDeliveryRuns_DeliversNothing()
    {
        Create().Report(0.5);

        Assert.Empty(_delivered);
    }

    /// <summary>
    /// A report after a delivery has run schedules a new delivery, so the last value is never lost.
    /// </summary>
    [Fact]
    public void Report_AfterDelivery_SchedulesNewDelivery()
    {
        var progress = Create();
        progress.Report(0.2);
        _queue.Dequeue()();

        progress.Report(1.0);
        Assert.Single(_queue);
        _queue.Dequeue()();

        Assert.Equal([0.2, 1.0], _delivered);
    }

    /// <summary>
    /// When scheduling the delivery fails the exception reaches the caller, and the next report
    /// schedules again instead of being swallowed by a stuck pending flag.
    /// </summary>
    [Fact]
    public void Report_PostThrows_RethrowsAndNextReportSchedulesAgain()
    {
        var fail = true;
        var progress = new CoalescingProgress(_delivered.Add, action =>
        {
            if (fail)
            {
                throw new InvalidOperationException("dispatcher gone");
            }

            _queue.Enqueue(action);
        });

        Assert.Throws<InvalidOperationException>(() => progress.Report(0.4));
        fail = false;
        progress.Report(0.6);
        _queue.Dequeue()();

        Assert.Equal(0.6, Assert.Single(_delivered), precision: 6);
    }

    /// <summary>
    /// Completing delivers the newest undelivered value at once, without waiting for the posted action.
    /// </summary>
    [Fact]
    public void Complete_ValuePending_DeliversNewestImmediately()
    {
        var progress = Create();
        progress.Report(0.2);
        progress.Report(0.9);

        progress.Complete();

        Assert.Equal(0.9, Assert.Single(_delivered), precision: 6);
    }

    /// <summary>
    /// A delivery that was queued before completion does nothing when it finally runs, so a late
    /// value cannot reach the callback.
    /// </summary>
    [Fact]
    public void Complete_QueuedDeliveryRunsLater_DeliversNothingMore()
    {
        var progress = Create();
        progress.Report(0.5);
        progress.Complete();

        _queue.Dequeue()();

        Assert.Equal(0.5, Assert.Single(_delivered), precision: 6);
    }

    /// <summary>
    /// Reports after completion are ignored and schedule nothing.
    /// </summary>
    [Fact]
    public void Report_AfterComplete_IsIgnored()
    {
        var progress = Create();
        progress.Complete();

        progress.Report(1.0);

        Assert.Empty(_queue);
        Assert.Empty(_delivered);
    }

    /// <summary>
    /// When everything was already delivered, completing delivers nothing again.
    /// </summary>
    [Fact]
    public void Complete_NothingUndelivered_DeliversNothing()
    {
        var progress = Create();
        progress.Report(0.3);
        _queue.Dequeue()();

        progress.Complete();

        Assert.Equal(0.3, Assert.Single(_delivered), precision: 6);
    }

    /// <summary>
    /// Completing twice delivers the value once.
    /// </summary>
    [Fact]
    public void Complete_CalledTwice_DeliversOnce()
    {
        var progress = Create();
        progress.Report(0.7);

        progress.Complete();
        progress.Complete();

        Assert.Single(_delivered);
    }
}
