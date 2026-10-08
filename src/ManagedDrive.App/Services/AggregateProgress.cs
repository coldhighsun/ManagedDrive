namespace ManagedDrive.App.Services;

/// <summary>
/// Combines the progress of several operations running side by side (the disks loaded at
/// startup) into one fraction for a single progress bar. Each operation reports its own fraction
/// in [0, 1] and counts towards the total in proportion to its weight (e.g. its image size), so a
/// large disk moves the bar more than a small one. Thread-safe; the combined fraction never goes
/// down.
/// </summary>
public sealed class AggregateProgress
{
    /// <summary>
    /// Guards <see cref="_fractions"/> and <see cref="_last"/>.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The latest fraction in [0, 1] reported by each operation.
    /// </summary>
    private readonly double[] _fractions;

    /// <summary>
    /// The relative weight of each operation; always positive.
    /// </summary>
    private readonly double[] _weights;

    /// <summary>
    /// Sum of <see cref="_weights"/>.
    /// </summary>
    private readonly double _totalWeight;

    /// <summary>
    /// Receives the combined fraction after every change.
    /// </summary>
    private readonly Action<double> _report;

    /// <summary>
    /// The last combined fraction passed to <see cref="_report"/>.
    /// </summary>
    private double _last;

    /// <summary>
    /// Initializes the aggregate for operations with the given weights.
    /// </summary>
    /// <param name="weights">
    /// One weight per operation, in the order the operations are numbered. A weight that is zero,
    /// negative or not a number counts as the average of the valid ones (or <c>1</c> when none is
    /// valid), so an operation of unknown size still takes its share of the bar.
    /// </param>
    /// <param name="report">
    /// Called with the combined fraction in [0, 1] whenever it grows. May be called from any
    /// thread that reports progress.
    /// </param>
    public AggregateProgress(IReadOnlyList<double> weights, Action<double> report)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(report);

        var valid = weights.Where(w => double.IsFinite(w) && w > 0).ToList();
        var fallback = valid.Count == 0 ? 1.0 : valid.Average();
        _weights = [.. weights.Select(w => double.IsFinite(w) && w > 0 ? w : fallback)];
        _fractions = new double[_weights.Length];
        _totalWeight = _weights.Sum();
        _report = report;
    }

    /// <summary>
    /// Gets the number of operations being combined.
    /// </summary>
    public int Count => _weights.Length;

    /// <summary>
    /// Creates the reporter an operation uses for its own progress.
    /// </summary>
    /// <param name="index">The operation's number, from <c>0</c> to <see cref="Count"/> - 1.</param>
    /// <returns>A reporter that updates the operation's share of the combined fraction.</returns>
    public IProgress<double> ForOperation(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return new OperationReporter(this, index);
    }

    /// <summary>
    /// Marks an operation as finished, whatever its outcome, so that a failed or cancelled one
    /// doesn't hold the bar short of the end.
    /// </summary>
    /// <param name="index">The operation's number.</param>
    public void Complete(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        Update(index, 1.0);
    }

    /// <summary>
    /// Stores one operation's fraction and reports the new combined fraction if it grew.
    /// </summary>
    /// <param name="index">The operation's number.</param>
    /// <param name="value">The operation's fraction; clamped to [0, 1].</param>
    private void Update(int index, double value)
    {
        double combined;
        lock (_lock)
        {
            _fractions[index] = Math.Clamp(double.IsNaN(value) ? 0.0 : value, 0.0, 1.0);

            var sum = 0.0;
            for (var i = 0; i < _fractions.Length; i++)
            {
                sum += _fractions[i] * _weights[i];
            }

            combined = Math.Clamp(sum / _totalWeight, 0.0, 1.0);
            if (combined <= _last)
            {
                return;
            }

            _last = combined;

            // Reported inside the lock so values reach the callback in increasing order.
            _report(combined);
        }
    }

    /// <summary>
    /// The <see cref="IProgress{T}"/> handed to one operation.
    /// </summary>
    /// <param name="owner">The aggregate it reports to.</param>
    /// <param name="index">The operation's number.</param>
    private sealed class OperationReporter(AggregateProgress owner, int index) : IProgress<double>
    {
        /// <inheritdoc />
        public void Report(double value) => owner.Update(index, value);
    }
}
