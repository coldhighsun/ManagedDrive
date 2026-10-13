namespace ManagedDrive.App.Services;

/// <summary>
/// Folds the interleaved per-disk progress reports of the parallel exit save into one stable,
/// monotonic view, so the exit overlay does not flip between disks. Every disk and its size is
/// known up front, so the byte total is fixed and the overall fraction is weighted by size, which
/// keeps the bar and the byte counts in step. Thread-safe: the disks report from their save
/// threads, and the UI reads the newest state with <see cref="Sample"/>.
/// </summary>
internal sealed class ExitSaveProgressTracker
{
    /// <summary>
    /// The share of the work shown (bar and bytes) until <see cref="Complete"/>: a disk that was
    /// clean when the sizes were collected can still be written to and saved without being
    /// tracked, so the other disks finishing must not show everything as done while it works.
    /// </summary>
    private const double UncompletedCeiling = 0.99;

    /// <summary>
    /// One disk's state. Only touched under <see cref="_lock"/>.
    /// </summary>
    /// <param name="totalBytes">Used bytes of the disk.</param>
    private sealed class DiskState(ulong totalBytes)
    {
        /// <summary>
        /// Gets the used bytes of the disk.
        /// </summary>
        public ulong TotalBytes { get; } = totalBytes;

        /// <summary>
        /// Gets or sets the furthest save fraction reached, in [0, 1].
        /// </summary>
        public double Fraction { get; set; }

        /// <summary>
        /// Gets or sets whether the disk's own final save has begun, i.e. it has reported.
        /// </summary>
        public bool Started { get; set; }

        /// <summary>
        /// Gets or sets whether its save failed; it then stays where it was and never counts as finished.
        /// </summary>
        public bool Failed { get; set; }

        /// <summary>
        /// Gets whether the disk is finished.
        /// </summary>
        public bool IsFinished => !Failed && Fraction >= 1.0;
    }

    /// <summary>
    /// Guards the state of every disk and the fields below.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The disks being written, by mount point.
    /// </summary>
    private readonly Dictionary<string, DiskState> _disks;

    /// <summary>
    /// The highest overall fraction shown so far.
    /// </summary>
    private double _overall;

    /// <summary>
    /// Whether <see cref="Complete"/> was called.
    /// </summary>
    private bool _completed;

    /// <summary>
    /// Creates a tracker for the disks about to be saved.
    /// </summary>
    /// <param name="totalBytesByMountPoint">Used bytes of every disk that will be written, keyed by mount point.</param>
    public ExitSaveProgressTracker(IReadOnlyDictionary<string, ulong> totalBytesByMountPoint)
    {
        _disks = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (mountPoint, totalBytes) in totalBytesByMountPoint)
        {
            _disks[mountPoint] = new(totalBytes);
        }
    }

    /// <summary>
    /// Gets how many disks are being written; zero when no disk had anything to save.
    /// </summary>
    public int DiskCount => _disks.Count;

    /// <summary>
    /// A point-in-time view of the exit save.
    /// </summary>
    /// <param name="Overall">Size-weighted progress in [0, 1], never lower than an earlier one.</param>
    /// <param name="DiskCount">How many disks are being saved.</param>
    /// <param name="CompletedCount">How many of them have finished.</param>
    /// <param name="DoneBytes">Bytes written so far across all disks.</param>
    /// <param name="TotalBytes">Used bytes of all disks.</param>
    /// <param name="MountPoint">The mount point of the only disk, or <c>null</c> when there are several.</param>
    /// <param name="IsFinishing">
    /// Whether every disk has finished (none failed) but the save is not over yet, because the
    /// caller is still wrapping up (e.g. waiting for unmounts already in flight).
    /// </param>
    /// <param name="FailedCount">How many disks' saves failed.</param>
    internal readonly record struct Snapshot(
        double Overall, int DiskCount, int CompletedCount, ulong DoneBytes, ulong TotalBytes, string? MountPoint,
        bool IsFinishing, int FailedCount)
    {
        /// <summary>
        /// Gets whether every disk is either finished or failed, with at least one failure, so
        /// nothing is left to wait for.
        /// </summary>
        public bool EndedWithFailures => FailedCount > 0 && CompletedCount + FailedCount == DiskCount;
    }

    /// <summary>
    /// Records one disk's progress. A disk that is not being tracked (it has nothing to write) or
    /// whose save failed is ignored. The first report of a disk marks its own final save as begun.
    /// </summary>
    /// <param name="mountPoint">The reporting disk.</param>
    /// <param name="diskFraction">That disk's save progress in [0, 1].</param>
    /// <returns>Whether the report changed anything the screen shows, i.e. a refresh is worthwhile.</returns>
    public bool Report(string mountPoint, double diskFraction)
    {
        lock (_lock)
        {
            if (!_disks.TryGetValue(mountPoint, out var disk) || disk.Failed)
            {
                return false;
            }

            var changed = !disk.Started;
            disk.Started = true;

            // Reports from several threads can arrive out of order.
            var fraction = Math.Clamp(diskFraction, 0.0, 1.0);
            if (fraction > disk.Fraction)
            {
                disk.Fraction = fraction;
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>
    /// Records that a disk's save failed, so it is not shown as finished. Only counts once the
    /// disk's own final save has begun (it has reported): <c>SaveFailed</c> is also raised by the
    /// periodic auto-save, which keeps running on disks still waiting their turn, and such a failure
    /// must not mark a disk whose final save then succeeds.
    /// </summary>
    /// <param name="mountPoint">The disk whose save failed.</param>
    public void MarkFailed(string mountPoint)
    {
        lock (_lock)
        {
            if (_disks.TryGetValue(mountPoint, out var disk) && disk.Started)
            {
                disk.Failed = true;
            }
        }
    }

    /// <summary>
    /// Marks every disk that did not fail as finished, for when the save returned without each one
    /// reporting 1 (e.g. a disk that left the mount list in between).
    /// </summary>
    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            foreach (var disk in _disks.Values)
            {
                if (!disk.Failed)
                {
                    disk.Fraction = 1.0;
                }
            }
        }
    }

    /// <summary>
    /// Gets the aggregated state as of now, and advances the highest overall fraction shown so far
    /// (so it is not a pure read: the fraction it returns never goes down).
    /// </summary>
    /// <returns>The current snapshot.</returns>
    public Snapshot Sample()
    {
        lock (_lock)
        {
            ulong done = 0;
            ulong total = 0;
            double fractionSum = 0;
            var completed = 0;
            var failed = 0;
            foreach (var disk in _disks.Values)
            {
                total += disk.TotalBytes;
                done += (ulong)(disk.TotalBytes * disk.Fraction);
                fractionSum += disk.Fraction;
                completed += disk.IsFinished ? 1 : 0;
                failed += disk.Failed ? 1 : 0;
            }

            // Bar and bytes share the ceiling so they cannot disagree while the save is wrapping up.
            // Rounded to the nearest byte so a tiny disk that is done does not read as unstarted.
            var ceiling = _completed ? 1.0 : UncompletedCeiling;
            done = Math.Min(done, (ulong)Math.Round(total * ceiling));

            // Disks with nothing to write have no weight; fall back to counting them equally.
            var overall = total > 0 ? (double)done / total : fractionSum / Math.Max(1, _disks.Count);
            _overall = Math.Max(_overall, Math.Min(Math.Clamp(overall, 0.0, 1.0), ceiling));

            return new(_overall, _disks.Count, completed, done, total,
                _disks.Count == 1 ? _disks.Keys.Single() : null,
                IsFinishing: _disks.Count > 0 && completed == _disks.Count,
                FailedCount: failed);
        }
    }
}
