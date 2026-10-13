namespace ManagedDrive.App.Services;

/// <summary>
/// Tells another thread (the UI thread) that "something changed, look again", merging signals that
/// arrive faster than it can act: while one refresh is still waiting, further signals add nothing.
/// For callers whose state lives elsewhere (read when the refresh runs); a value-carrying report
/// is <see cref="CoalescingProgress"/>.
/// </summary>
/// <param name="refresh">Runs on the thread chosen by <paramref name="post"/>, at most once per burst.</param>
/// <param name="post">Schedules the refresh on that thread.</param>
public sealed class CoalescingSignal(Action refresh, Action<Action> post)
{
    /// <summary>
    /// The coalescer doing the work; the value it carries is not used.
    /// </summary>
    private readonly CoalescingProgress _inner = new(_ => refresh(), post);

    /// <summary>
    /// Requests a refresh. Safe to call from any thread.
    /// </summary>
    public void Notify() => _inner.Report(0);

    /// <summary>
    /// Runs the refresh now if one is still waiting, on the calling thread (which must be the one
    /// the refresh expects), and ignores every later signal and any refresh still queued.
    /// </summary>
    public void Complete() => _inner.Complete();
}
