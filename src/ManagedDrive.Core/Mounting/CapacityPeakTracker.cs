namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Tracks the largest capacity a disk had while a long write (a snapshot, an export) was running.
/// The capacity can grow while such a write runs, and data the larger capacity let in
/// may end up in it, so a header holding the capacity read when the write started could be smaller
/// than a node it holds, which then fails to load. Thread-safe; any number of watches may be open.
/// </summary>
internal sealed class CapacityPeakTracker
{
    /// <summary>
    /// Guards <see cref="_active"/> and each watch's peak.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The watches that are open now.
    /// </summary>
    private readonly List<Watch> _active = [];

    /// <summary>
    /// Opens a watch whose peak starts at the capacity <paramref name="readCurrent"/> reports.
    /// The current capacity is read under the tracker's lock, so a capacity published before it is
    /// seen by that read, and one published after it reaches the watch through
    /// <see cref="Raise"/>, as long as it is published before <see cref="Raise"/> is called.
    /// </summary>
    /// <param name="readCurrent">Reads the capacity in effect now.</param>
    /// <returns>The watch; dispose it when the write is done.</returns>
    public Watch Start(Func<ulong> readCurrent)
    {
        ArgumentNullException.ThrowIfNull(readCurrent);

        lock (_lock)
        {
            var watch = new Watch(this, readCurrent());
            _active.Add(watch);
            return watch;
        }
    }

    /// <summary>
    /// Records a capacity that was just published, raising the peak of every open watch below it.
    /// Must be called after the capacity is published and before the file system's ceiling is
    /// raised to it.
    /// </summary>
    /// <param name="capacity">The published capacity.</param>
    public void Raise(ulong capacity)
    {
        lock (_lock)
        {
            foreach (var watch in _active)
            {
                if (capacity > watch.PeakCore)
                {
                    watch.PeakCore = capacity;
                }
            }
        }
    }

    /// <summary>
    /// Removes a watch from the open ones.
    /// </summary>
    /// <param name="watch">The watch to close.</param>
    private void Stop(Watch watch)
    {
        lock (_lock)
        {
            _active.Remove(watch);
        }
    }

    /// <summary>
    /// One open watch of a <see cref="CapacityPeakTracker"/>.
    /// </summary>
    internal sealed class Watch : IDisposable
    {
        /// <summary>
        /// The tracker the watch belongs to.
        /// </summary>
        private readonly CapacityPeakTracker _owner;

        /// <summary>
        /// Initializes a new instance of the <see cref="Watch"/> class.
        /// </summary>
        /// <param name="owner">The tracker the watch belongs to.</param>
        /// <param name="initial">The capacity in effect when the watch was opened.</param>
        internal Watch(CapacityPeakTracker owner, ulong initial)
        {
            _owner = owner;
            PeakCore = initial;
        }

        /// <summary>
        /// Gets the largest capacity seen since the watch was opened.
        /// </summary>
        public ulong Peak
        {
            get
            {
                lock (_owner._lock)
                {
                    return PeakCore;
                }
            }
        }

        /// <summary>
        /// Gets or sets the peak; only accessed under the owner's lock.
        /// </summary>
        internal ulong PeakCore { get; set; }

        /// <summary>
        /// Closes the watch.
        /// </summary>
        public void Dispose() => _owner.Stop(this);
    }
}
