namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="BackgroundPriorityScope"/>. Each test runs on its own thread so changing
/// the priority never touches a thread the test runner reuses.
/// </summary>
public sealed class BackgroundPriorityScopeTests
{
    /// <summary>
    /// A normal-priority thread runs below normal inside the scope and is restored afterwards.
    /// </summary>
    [Fact]
    public void Enter_NormalPriorityThread_LowersPriorityUntilDisposed()
    {
        var (inside, after) = RunOnNewThread(ThreadPriority.Normal, enabled: true);

        Assert.Equal(ThreadPriority.BelowNormal, inside);
        Assert.Equal(ThreadPriority.Normal, after);
    }

    /// <summary>
    /// The original priority comes back even when it was above normal.
    /// </summary>
    [Fact]
    public void Enter_AboveNormalThread_RestoresAboveNormalOnDispose()
    {
        var (inside, after) = RunOnNewThread(ThreadPriority.AboveNormal, enabled: true);

        Assert.Equal(ThreadPriority.BelowNormal, inside);
        Assert.Equal(ThreadPriority.AboveNormal, after);
    }

    /// <summary>
    /// A thread that already runs lower than below normal is not raised.
    /// </summary>
    [Fact]
    public void Enter_ThreadAlreadyLowest_LeavesPriorityUnchanged()
    {
        var (inside, after) = RunOnNewThread(ThreadPriority.Lowest, enabled: true);

        Assert.Equal(ThreadPriority.Lowest, inside);
        Assert.Equal(ThreadPriority.Lowest, after);
    }

    /// <summary>
    /// A disabled scope changes nothing, and disposing it does not touch the priority.
    /// </summary>
    [Fact]
    public void EnterIf_Disabled_LeavesPriorityUnchanged()
    {
        var (inside, after) = RunOnNewThread(ThreadPriority.AboveNormal, enabled: false);

        Assert.Equal(ThreadPriority.AboveNormal, inside);
        Assert.Equal(ThreadPriority.AboveNormal, after);
    }

    /// <summary>
    /// Runs a scope on a new thread started at <paramref name="start"/>.
    /// </summary>
    /// <param name="start">The priority the thread starts with.</param>
    /// <param name="enabled">Whether the scope lowers the priority.</param>
    /// <returns>The thread's priority inside the scope and after it was disposed.</returns>
    private static (ThreadPriority Inside, ThreadPriority After) RunOnNewThread(ThreadPriority start, bool enabled)
    {
        var result = (Inside: ThreadPriority.Normal, After: ThreadPriority.Normal);
        var thread = new Thread(() =>
        {
            var scope = BackgroundPriorityScope.EnterIf(enabled);
            result.Inside = Thread.CurrentThread.Priority;
            scope.Dispose();
            result.After = Thread.CurrentThread.Priority;
        })
        {
            Priority = start,
        };

        thread.Start();
        thread.Join();
        return result;
    }
}
