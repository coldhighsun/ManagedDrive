namespace ManagedDrive.App.Services;

/// <summary>
/// Runs a slow notification (the WM_SETTINGCHANGE broadcast, which waits on every top-level window
/// and can take seconds when one is hung) on the thread pool instead of the caller's thread. Requests
/// that arrive while one is already waiting to run are merged into it, so a burst costs at most one
/// extra run.
/// </summary>
/// <param name="send">The notification to send.</param>
public sealed class CoalescingBroadcaster(Action send)
{
    /// <summary>
    /// Guards <see cref="_pending"/> and <see cref="_last"/>.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Whether a run is queued but has not started yet; further requests then have nothing to add.
    /// </summary>
    private bool _pending;

    /// <summary>
    /// The most recently queued run. Runs are chained, so waiting for it waits for all of them.
    /// </summary>
    private Task _last = Task.CompletedTask;

    /// <summary>
    /// Asks for the notification to be sent and returns immediately.
    /// </summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_pending)
            {
                return;
            }

            _pending = true;
            _last = _last.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        _pending = false;
                    }

                    try
                    {
                        send();
                    }
                    catch (Exception ex)
                    {
                        // A notification is best effort; the registry change it announces is already made.
                        AppLog.CreateLogger<CoalescingBroadcaster>().LogDebug(ex, "Environment change broadcast failed.");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Waits for the requested notifications to finish, for at most <paramref name="timeout"/>.
    /// Used at exit so the last change is announced before the process ends.
    /// </summary>
    /// <param name="timeout">The longest to wait.</param>
    /// <returns><c>true</c> if everything requested has been sent.</returns>
    public bool Wait(TimeSpan timeout)
    {
        Task last;
        lock (_gate)
        {
            last = _last;
        }

        return last.Wait(timeout);
    }
}
