using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="StartupCompletion"/>, the order in which the end of the startup auto-mount is
/// announced.
/// </summary>
public sealed class StartupCompletionTests
{
    /// <summary>
    /// The waiters are released before the splash close starts, so a slow close cannot hold them.
    /// </summary>
    [Fact]
    public async Task ReleaseThenCloseSplashAsync_Normal_ReleasesBeforeCloseStarts()
    {
        var released = new TaskCompletionSource();
        var releasedWhenCloseStarted = false;

        await StartupCompletion.ReleaseThenCloseSplashAsync(released, () =>
        {
            releasedWhenCloseStarted = released.Task.IsCompleted;
            return Task.CompletedTask;
        });

        Assert.True(releasedWhenCloseStarted);
    }

    /// <summary>
    /// A close that is still pending does not keep the waiters waiting.
    /// </summary>
    [Fact]
    public async Task ReleaseThenCloseSplashAsync_CloseStillPending_WaitersAlreadyReleased()
    {
        var released = new TaskCompletionSource();
        var closeGate = new TaskCompletionSource();

        var running = StartupCompletion.ReleaseThenCloseSplashAsync(released, () => closeGate.Task);

        Assert.True(released.Task.IsCompleted);
        Assert.False(running.IsCompleted);

        closeGate.SetResult();
        await running;
    }

    /// <summary>
    /// If the close throws, the waiters were already released and the error reaches the caller.
    /// </summary>
    [Fact]
    public async Task ReleaseThenCloseSplashAsync_CloseThrows_StillReleasedAndPropagates()
    {
        var released = new TaskCompletionSource();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupCompletion.ReleaseThenCloseSplashAsync(released, () => throw new InvalidOperationException("close failed")));

        Assert.True(released.Task.IsCompletedSuccessfully);
    }
}
