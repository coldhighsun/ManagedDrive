namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Limits how often a loop that finishes one small unit of work at a time (one node of a disk)
/// reports progress, so a disk with tens of thousands of files does not send tens of thousands of
/// reports to the UI thread. Not thread-safe: one instance belongs to one sequential loop.
/// </summary>
internal sealed class ProgressThrottle
{
    /// <summary>
    /// The smallest growth of the fraction that is worth reporting (0.5%).
    /// </summary>
    internal const double MinimumStep = 0.005;

    /// <summary>
    /// The fraction passed to the last report that went through.
    /// </summary>
    private double _lastReported = -1.0;

    /// <summary>
    /// Reports <paramref name="fraction"/> to <paramref name="progress"/> when it grew by at least
    /// <see cref="MinimumStep"/> since the last report that went through, or reaches 1.0. Every
    /// call checks cancellation first when <paramref name="progress"/> supports it
    /// (<see cref="ICancellationCheck"/>), so skipping a report never delays a cancel.
    /// </summary>
    /// <param name="progress">The reporter, or <see langword="null"/> for none.</param>
    /// <param name="fraction">The current fraction in [0, 1].</param>
    public void Report(IProgress<double>? progress, double fraction)
    {
        if (progress is null)
        {
            return;
        }

        (progress as ICancellationCheck)?.ThrowIfCancellationRequested();

        if (fraction < 1.0 && fraction - _lastReported < MinimumStep)
        {
            return;
        }

        _lastReported = fraction;
        progress.Report(fraction);
    }
}
