namespace ManagedDrive.App.Services;

/// <summary>
/// Passes progress on to a callback that runs somewhere else (the UI thread), merging reports that
/// arrive faster than it can take them: while one delivery is still waiting only the newest value
/// is kept. Unlike <c>Progress&lt;T&gt;</c> it does not queue one dispatcher item per report, and it
/// lets the caller choose a low dispatcher priority so a flood of reports from loading threads
/// cannot starve mouse and keyboard input.
/// </summary>
/// <param name="report">Receives the newest value, once per delivery.</param>
/// <param name="post">Schedules the delivery on the thread that should run <paramref name="report"/>.</param>
public sealed class CoalescingProgress(Action<double> report, Action<Action> post) : IProgress<double>
{
    /// <summary>
    /// Guards <see cref="_latest"/>, <see cref="_undelivered"/>, <see cref="_pending"/> and
    /// <see cref="_completed"/>.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The newest value reported.
    /// </summary>
    private double _latest;

    /// <summary>
    /// Whether <see cref="_latest"/> has not been handed to the callback yet.
    /// </summary>
    private bool _undelivered;

    /// <summary>
    /// Whether a delivery is scheduled and has not run yet.
    /// </summary>
    private bool _pending;

    /// <summary>
    /// Whether <see cref="Complete"/> was called; later reports and queued deliveries are ignored.
    /// </summary>
    private bool _completed;

    /// <summary>
    /// <see cref="Deliver"/> as a delegate, created once instead of for every delivery. Assigned
    /// without synchronization on purpose: two threads racing to create it just build equal
    /// delegates for the same method, and <see cref="_pending"/> still allows one delivery at a time.
    /// </summary>
    private Action? _deliver;

    /// <inheritdoc />
    public void Report(double value)
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _latest = value;
            _undelivered = true;
            if (_pending)
            {
                return;
            }

            _pending = true;
        }

        try
        {
            post(_deliver ??= Deliver);
        }
        catch
        {
            // Nothing was scheduled, so the flag must not stay set: it would swallow every later
            // report. The failure itself is not ours to handle.
            lock (_lock)
            {
                _pending = false;
            }

            throw;
        }
    }

    /// <summary>
    /// Ends the reporting: hands the newest value that was not delivered yet to the callback right
    /// now, on the calling thread (which must be the one the callback expects), and ignores every
    /// later report and any delivery still queued. Does nothing when already completed.
    /// </summary>
    public void Complete()
    {
        double value;
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (!_undelivered)
            {
                return;
            }

            _undelivered = false;
            value = _latest;
        }

        report(value);
    }

    /// <summary>
    /// Hands the newest value to the callback and allows the next delivery to be scheduled.
    /// </summary>
    private void Deliver()
    {
        double value;
        lock (_lock)
        {
            // Cleared before the value is read, so a report arriving from here on schedules a new
            // delivery instead of being lost.
            _pending = false;
            if (_completed || !_undelivered)
            {
                return;
            }

            _undelivered = false;
            value = _latest;
        }

        report(value);
    }
}
