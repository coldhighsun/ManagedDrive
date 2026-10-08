using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="AutoMountScheduler"/>, which decides the order in which the disks loaded at
/// startup are added and prompted for.
/// </summary>
public sealed class AutoMountSchedulerTests
{
    /// <summary>
    /// Results are finished as their loads complete; with loads completing in index order that
    /// is index order.
    /// </summary>
    [Fact]
    public async Task RunAsync_NoPasswords_FinishesInCompletionOrder()
    {
        var finished = new List<int>();

        await AutoMountScheduler.RunAsync(
            4, 2, CompleteInOrder(4), _ => false,
            (i, _) => { finished.Add(i); return Task.CompletedTask; },
            (_, _) => { },
            (_, _) => { });

        Assert.Equal([0, 1, 2, 3], finished);
    }

    /// <summary>
    /// A fast load is finished without waiting for a slower one that comes before it.
    /// </summary>
    [Fact]
    public async Task RunAsync_SlowFirstLoad_FinishesFasterLoadFirst()
    {
        var finished = new List<int>();
        var release = new TaskCompletionSource();

        await AutoMountScheduler.RunAsync(
            2, 2,
            async i =>
            {
                if (i == 0)
                {
                    await release.Task;
                }

                return i;
            },
            _ => false,
            (i, _) =>
            {
                finished.Add(i);
                if (i == 1)
                {
                    release.SetResult();
                }

                return Task.CompletedTask;
            },
            (_, _) => { },
            (_, _) => { });

        Assert.Equal([1, 0], finished);
    }

    /// <summary>
    /// A result needing a password is finished after all the others, even when it comes first.
    /// </summary>
    [Fact]
    public async Task RunAsync_PasswordFirst_IsFinishedLast()
    {
        var finished = new List<int>();

        await AutoMountScheduler.RunAsync(
            4, 2, CompleteInOrder(4), i => i is 0 or 2,
            (i, _) => { finished.Add(i); return Task.CompletedTask; },
            (_, _) => { },
            (_, _) => { });

        Assert.Equal([1, 3, 0, 2], finished);
    }

    /// <summary>
    /// A load that breaks the no-throw contract is reported and the other disks are still finished.
    /// </summary>
    [Fact]
    public async Task RunAsync_LoadThrows_ReportsErrorAndFinishesTheRest()
    {
        var finished = new List<int>();
        var errors = new List<int>();

        await AutoMountScheduler.RunAsync(
            3, 2,
            i => i == 1 ? Task.FromException<int>(new InvalidOperationException("boom")) : Task.FromResult(i),
            _ => false,
            (i, _) => { finished.Add(i); return Task.CompletedTask; },
            (_, _) => { },
            (i, _) => errors.Add(i));

        Assert.Equal([0, 2], finished.Order());
        Assert.Equal([1], errors);
    }

    /// <summary>
    /// Builds a load whose tasks complete strictly in index order, so the order results are
    /// finished in doesn't depend on how already-completed tasks happen to be picked.
    /// </summary>
    /// <param name="count">How many loads there are.</param>
    /// <returns>A load returning its own index once the previous load has completed.</returns>
    private static Func<int, Task<int>> CompleteInOrder(int count)
    {
        var done = Enumerable.Range(0, count)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        return async i =>
        {
            if (i > 0)
            {
                await done[i - 1].Task;
            }

            done[i].SetResult();
            return i;
        };
    }

    /// <summary>
    /// No more loads than the limit run at the same time. Only the upper bound is asserted: how
    /// many actually overlap depends on thread-pool timing.
    /// </summary>
    [Fact]
    public async Task RunAsync_ManyLoads_NeverExceedsConcurrencyLimit()
    {
        var running = 0;
        var peak = 0;

        await AutoMountScheduler.RunAsync(
            6, 2,
            async i =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref running);
                return i;
            },
            _ => false, (_, _) => Task.CompletedTask, (_, _) => { }, (_, _) => { });

        Assert.InRange(peak, 1, 2);
    }

    /// <summary>
    /// A result whose finishing throws is reported and doesn't stop the others being finished.
    /// </summary>
    [Fact]
    public async Task RunAsync_FinishThrows_ReportsErrorAndFinishesTheRest()
    {
        var finished = new List<int>();
        var errors = new List<int>();

        await AutoMountScheduler.RunAsync(
            3, 2, i => Task.FromResult(i), _ => false,
            (i, _) =>
            {
                if (i == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                finished.Add(i);
                return Task.CompletedTask;
            },
            (i, _) => errors.Add(i),
            (_, _) => { });

        Assert.Equal([0, 2], finished);
        Assert.Equal([1], errors);
    }

    /// <summary>
    /// A concurrency below one is rejected.
    /// </summary>
    [Fact]
    public async Task RunAsync_ZeroConcurrency_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AutoMountScheduler.RunAsync(
            1, 0, i => Task.FromResult(i), _ => false, (_, _) => Task.CompletedTask, (_, _) => { }, (_, _) => { }));
    }

    /// <summary>
    /// Raises <paramref name="target"/> to <paramref name="value"/> if that is larger.
    /// </summary>
    /// <param name="target">The shared maximum.</param>
    /// <param name="value">The candidate.</param>
    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
        }
        while (value > current && Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
