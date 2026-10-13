using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="CoalescingSignal"/>, which merges bursts of "changed" signals into one refresh.
/// </summary>
public sealed class CoalescingSignalTests
{
    /// <summary>
    /// Scheduled refreshes that have not run yet, standing in for the dispatcher queue.
    /// </summary>
    private readonly Queue<Action> _queue = new();

    /// <summary>
    /// How many times the refresh ran.
    /// </summary>
    private int _refreshes;

    /// <summary>
    /// Creates the signal under test, wired to the fake queue.
    /// </summary>
    private CoalescingSignal Create() => new(() => _refreshes++, _queue.Enqueue);

    /// <summary>
    /// A burst of signals schedules one refresh.
    /// </summary>
    [Fact]
    public void Notify_Burst_RefreshesOnce()
    {
        var signal = Create();

        signal.Notify();
        signal.Notify();
        signal.Notify();
        _queue.Dequeue()();

        Assert.Empty(_queue);
        Assert.Equal(1, _refreshes);
    }

    /// <summary>
    /// A signal after the refresh ran schedules the next one.
    /// </summary>
    [Fact]
    public void Notify_AfterRefresh_SchedulesAnother()
    {
        var signal = Create();
        signal.Notify();
        _queue.Dequeue()();

        signal.Notify();

        Assert.Single(_queue);
    }

    /// <summary>
    /// Completing runs a waiting refresh now and drops the queued one.
    /// </summary>
    [Fact]
    public void Complete_WithPendingSignal_RefreshesNowAndDropsTheQueuedOne()
    {
        var signal = Create();
        signal.Notify();

        signal.Complete();
        _queue.Dequeue()();

        Assert.Equal(1, _refreshes);
    }
}
