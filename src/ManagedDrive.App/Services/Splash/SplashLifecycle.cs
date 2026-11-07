namespace ManagedDrive.App.Services;

/// <summary>
/// Sequences the end of the startup splash: it stays up for at least
/// <see cref="SplashPolicy.MinimumDisplay"/>, is closed, and the main window is shown in its
/// place unless the app starts minimized. Free of WPF types (the window and the clock are passed
/// in as delegates), so the ordering rules can be unit tested.
/// </summary>
internal sealed class SplashLifecycle
{
    /// <summary>
    /// Whether the main window stays hidden after the splash closes (a minimized start).
    /// </summary>
    private readonly bool _startMinimized;

    /// <summary>
    /// Returns how long the splash has been up.
    /// </summary>
    private readonly Func<TimeSpan> _elapsed;

    /// <summary>
    /// Waits for the given time; <see cref="Task.Delay(TimeSpan)"/> in the app.
    /// </summary>
    private readonly Func<TimeSpan, Task> _delay;

    /// <summary>
    /// Shows the main window.
    /// </summary>
    private readonly Action _showMainWindow;

    /// <summary>
    /// Completed by <see cref="CloseAsync"/> once the splash is gone and the main window handled.
    /// </summary>
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Closes the splash window; set by <see cref="Show"/>.
    /// </summary>
    private Action? _closeSplash;

    /// <summary>
    /// Whether a <see cref="CloseAsync"/> call has taken over the splash and is still finishing.
    /// </summary>
    private bool _closing;

    /// <summary>
    /// Initializes the lifecycle.
    /// </summary>
    /// <param name="startMinimized">Whether the main window stays hidden after the splash.</param>
    /// <param name="elapsed">Returns how long the splash has been up.</param>
    /// <param name="delay">Waits for the given time.</param>
    /// <param name="showMainWindow">Shows the main window.</param>
    public SplashLifecycle(bool startMinimized, Func<TimeSpan> elapsed, Func<TimeSpan, Task> delay, Action showMainWindow)
    {
        _startMinimized = startMinimized;
        _elapsed = elapsed;
        _delay = delay;
        _showMainWindow = showMainWindow;
    }

    /// <summary>
    /// Gets a task that completes once the splash is gone and the main window has been shown (or
    /// deliberately kept hidden), or at once if there was never a splash. Lets code that picks
    /// between a dialog and a balloon by the main window's visibility wait for the real answer.
    /// </summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Records that a splash is up.
    /// </summary>
    /// <param name="closeSplash">Closes the splash window.</param>
    public void Show(Action closeSplash)
    {
        _closeSplash = closeSplash;
    }

    /// <summary>
    /// Closes the splash, if one is up, after it has been shown for its minimum time, then shows
    /// the main window unless the app starts minimized. Without a splash it only completes
    /// <see cref="Closed"/>. A second call while the first is still finishing does nothing.
    /// </summary>
    /// <returns>A task that completes when the splash is closed and the main window handled.</returns>
    public async Task CloseAsync()
    {
        var close = _closeSplash;
        if (close == null)
        {
            if (!_closing)
            {
                _closed.TrySetResult();
            }

            return;
        }

        // Cleared first so that a later call finds nothing to close while the minimum display time
        // is still being waited out.
        _closeSplash = null;
        _closing = true;

        try
        {
            var remaining = SplashPolicy.RemainingDisplay(_elapsed());
            if (remaining > TimeSpan.Zero)
            {
                await _delay(remaining);
            }

            close();
            if (!_startMinimized)
            {
                _showMainWindow();
            }
        }
        finally
        {
            // Also when closing or showing threw: waiters on Closed must not hang, and a later
            // call must not mistake this one for still being in progress.
            _closing = false;
            _closed.TrySetResult();
        }
    }
}
