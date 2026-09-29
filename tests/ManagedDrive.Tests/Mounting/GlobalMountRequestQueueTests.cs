using System.Collections.Concurrent;
using ManagedDrive.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="GlobalMountRequestQueue"/>.
/// </summary>
public sealed class GlobalMountRequestQueueTests
{
    /// <summary>
    /// A slow publish must finish before the unpublish and re-publish queued behind it run, so the
    /// helper sees the requests in the order the TEMP state changed.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task Enqueue_SlowFirstRequest_RunsRequestsInQueueOrder()
    {
        var queue = new GlobalMountRequestQueue(NullLogger.Instance);
        var order = new ConcurrentQueue<string>();
        using var releaseFirst = new ManualResetEventSlim();

        queue.Enqueue(() =>
        {
            releaseFirst.Wait(TestContext.Current.CancellationToken);
            order.Enqueue("publish");
        });
        queue.Enqueue(() => order.Enqueue("unpublish"));
        queue.Enqueue(() => order.Enqueue("publish again"));
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        releaseFirst.Set();
        await queue.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["publish", "unpublish", "publish again"], order);
    }

    /// <summary>
    /// A request that throws doesn't stop the requests queued after it.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task Enqueue_RequestThrows_StillRunsLaterRequests()
    {
        var queue = new GlobalMountRequestQueue(NullLogger.Instance);
        var ran = false;

        queue.Enqueue(() => throw new InvalidOperationException("helper failed"));
        queue.Enqueue(() => ran = true);
        await queue.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(ran);
    }
}
