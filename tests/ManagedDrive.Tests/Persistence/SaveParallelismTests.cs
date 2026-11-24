namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="SaveParallelism"/>.
/// </summary>
public sealed class SaveParallelismTests
{
    /// <summary>
    /// A save that is not interactive (exit save, logoff save) uses every logical core.
    /// </summary>
    [Fact]
    public void Max_NotInteractive_IsProcessorCount()
    {
        Assert.False(SaveParallelism.IsInteractive);
        Assert.Equal(Environment.ProcessorCount, SaveParallelism.Max);
    }

    /// <summary>
    /// An interactive save is limited to half of the logical cores, but never below 2.
    /// </summary>
    [Fact]
    public void Max_Interactive_IsHalfOfProcessorsAndAtLeastTwo()
    {
        using var scope = SaveParallelism.BeginInteractive();

        Assert.Equal(Math.Max(2, Environment.ProcessorCount / 2), SaveParallelism.Max);
    }

    /// <summary>
    /// The mode ends with the scope.
    /// </summary>
    [Fact]
    public void BeginInteractive_Disposed_RestoresPreviousMode()
    {
        SaveParallelism.BeginInteractive().Dispose();

        Assert.False(SaveParallelism.IsInteractive);
    }

    /// <summary>
    /// The mode follows the call chain into thread-pool work, which is where the compression runs.
    /// </summary>
    [Fact]
    public async Task BeginInteractive_WorkQueuedWithTaskRun_SeesInteractiveMode()
    {
        using var scope = SaveParallelism.BeginInteractive();

        var seen = await Task.Run(() => (SaveParallelism.IsInteractive, SaveParallelism.Max));

        Assert.True(seen.IsInteractive);
        Assert.Equal(SaveParallelism.InteractiveMax, seen.Max);
    }

    /// <summary>
    /// The mode also reaches the bodies of a parallel loop.
    /// </summary>
    [Fact]
    public void BeginInteractive_ParallelLoopBodies_SeeInteractiveMode()
    {
        using var scope = SaveParallelism.BeginInteractive();
        var interactiveBodies = 0;

        Parallel.For(0, 64, SaveParallelism.Options, _ =>
        {
            if (SaveParallelism.IsInteractive)
            {
                Interlocked.Increment(ref interactiveBodies);
            }
        });

        Assert.Equal(64, interactiveBodies);
    }

    /// <summary>
    /// Options reflect the current mode and each call returns a separate instance.
    /// </summary>
    [Fact]
    public void Options_ReturnsNewInstanceWithCurrentMax()
    {
        using var scope = SaveParallelism.BeginInteractive();

        var first = SaveParallelism.Options;
        var second = SaveParallelism.Options;

        Assert.NotSame(first, second);
        Assert.Equal(SaveParallelism.Max, first.MaxDegreeOfParallelism);
    }

    /// <summary>
    /// A worker keeps its priority when the save is not interactive.
    /// </summary>
    [Fact]
    public void EnterWorker_NotInteractive_LeavesPriorityUnchanged()
    {
        var priority = RunOnNewThread();

        Assert.Equal(ThreadPriority.Normal, priority);
    }

    /// <summary>
    /// In interactive mode the worker runs below normal priority.
    /// </summary>
    [Fact]
    public void EnterWorker_Interactive_LowersPriority()
    {
        using var scope = SaveParallelism.BeginInteractive();

        var priority = RunOnNewThread();

        Assert.Equal(ThreadPriority.BelowNormal, priority);
    }

    /// <summary>
    /// Runs <see cref="SaveParallelism.EnterWorker"/> on a new thread, which inherits the caller's
    /// execution context.
    /// </summary>
    /// <returns>The priority of that thread inside the scope.</returns>
    private static ThreadPriority RunOnNewThread()
    {
        var result = ThreadPriority.Normal;
        var thread = new Thread(() =>
        {
            using var worker = SaveParallelism.EnterWorker();
            result = Thread.CurrentThread.Priority;
        });

        thread.Start();
        thread.Join();
        return result;
    }
}
