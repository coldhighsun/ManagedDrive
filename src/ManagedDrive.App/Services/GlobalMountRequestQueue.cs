namespace ManagedDrive.App.Services;

/// <summary>
/// Runs <see cref="GlobalMountCoordinator"/>'s helper-service requests one at a time, in the order
/// they were queued, off the UI thread. Independent <see cref="Task.Run(Action)"/> calls could
/// run a quick publish → unpublish → publish of the same drive out of order and leave the global
/// symlink in the wrong state (e.g. removed while the disk is still the TEMP target).
/// </summary>
/// <param name="logger">Records a request that throws, so the requests queued after it still run.</param>
internal sealed class GlobalMountRequestQueue(ILogger logger)
{
    /// <summary>
    /// Guards <see cref="_tail"/>, since requests are queued from the UI thread but tests and
    /// future callers may not be.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// Completes once every request queued so far has run.
    /// </summary>
    private Task _tail = Task.CompletedTask;

    /// <summary>
    /// Queues <paramref name="request"/> to run on the thread pool after every request queued
    /// before it has finished.
    /// </summary>
    /// <param name="request">The blocking helper-service call to make.</param>
    public void Enqueue(Action request)
    {
        lock (_lock)
        {
            _tail = _tail.ContinueWith(
                _ => Run(request),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Returns a task that completes once every request queued so far has run.
    /// </summary>
    /// <returns>The task for the most recently queued request.</returns>
    public Task WhenIdle()
    {
        lock (_lock)
        {
            return _tail;
        }
    }

    /// <summary>
    /// Runs one request, logging a failure instead of letting it fault the chain — each request
    /// is best-effort, and one failing must not stop the ones queued after it.
    /// </summary>
    /// <param name="request">The request to run.</param>
    private void Run(Action request)
    {
        try
        {
            request();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GlobalMount] helper request failed.");
        }
    }
}
