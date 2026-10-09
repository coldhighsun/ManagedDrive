namespace ManagedDrive.App.Services;

/// <summary>
/// The order in which the end of the startup auto-mount is announced. Kept apart from
/// <c>App</c> so the order can be unit tested.
/// </summary>
internal static class StartupCompletion
{
    /// <summary>
    /// Releases what waits for the auto-mount (the CLI commands queued during startup), then runs
    /// the splash close. Releasing comes first and does not depend on the close: the commands need
    /// neither the splash nor the main window, and a close that is slow (the splash's minimum
    /// display time) or fails must not hold them back.
    /// </summary>
    /// <param name="released">Completed to release the waiters.</param>
    /// <param name="closeSplash">Closes the splash and shows the main window.</param>
    /// <returns>A task that completes when <paramref name="closeSplash"/> has finished.</returns>
    public static async Task ReleaseThenCloseSplashAsync(TaskCompletionSource released, Func<Task> closeSplash)
    {
        released.TrySetResult();
        await closeSplash();
    }
}
