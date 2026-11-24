namespace ManagedDrive.Core.Persistence;

/// <summary>
/// Runs the calling thread at <see cref="ThreadPriority.BelowNormal"/> until disposed, then puts
/// the previous priority back. Compressing a large disk image keeps most cores busy for tens of
/// seconds; at normal priority those worker threads compete on equal terms with the UI thread
/// and the renderer, which then wait for a time slice (hover effects and animations lag). At a
/// lower priority Windows lets the UI thread preempt a worker at once, and the save only slows
/// down when something else needs the core. Meant for thread-pool threads, so it must always be
/// disposed: a pool thread would otherwise carry the lower priority into unrelated work.
/// </summary>
internal readonly struct BackgroundPriorityScope : IDisposable
{
    /// <summary>
    /// Whether this scope lowered the priority and so has to restore it.
    /// </summary>
    private readonly bool _active;

    /// <summary>
    /// The priority the thread had before <see cref="Enter"/>, restored on <see cref="Dispose"/>.
    /// </summary>
    private readonly ThreadPriority _previous;

    /// <summary>
    /// Records the state to restore.
    /// </summary>
    /// <param name="active">Whether the scope changed the priority.</param>
    /// <param name="previous">The priority to restore.</param>
    private BackgroundPriorityScope(bool active, ThreadPriority previous)
    {
        _active = active;
        _previous = previous;
    }

    /// <summary>
    /// Lowers the calling thread to <see cref="ThreadPriority.BelowNormal"/> unless it already runs
    /// at or below that.
    /// </summary>
    /// <returns>A scope whose disposal restores the previous priority.</returns>
    public static BackgroundPriorityScope Enter()
    {
        var thread = Thread.CurrentThread;
        var previous = thread.Priority;
        if (previous > ThreadPriority.BelowNormal)
        {
            thread.Priority = ThreadPriority.BelowNormal;
        }

        return new(active: true, previous);
    }

    /// <summary>
    /// Lowers the calling thread like <see cref="Enter"/> when <paramref name="enabled"/> is set, and
    /// returns a scope that does nothing otherwise.
    /// </summary>
    /// <param name="enabled">Whether to lower the priority.</param>
    /// <returns>A scope whose disposal restores the previous priority, if it was lowered.</returns>
    public static BackgroundPriorityScope EnterIf(bool enabled) => enabled ? Enter() : default;

    /// <summary>
    /// Restores the priority the thread had before <see cref="Enter"/>, if this scope changed it.
    /// </summary>
    public void Dispose()
    {
        if (_active)
        {
            Thread.CurrentThread.Priority = _previous;
        }
    }
}
