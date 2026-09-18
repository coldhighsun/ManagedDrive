namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Manages the collection of active <see cref="RamDisk"/> instances.
/// Thread-safe; raises events when disks are mounted or unmounted.
/// Dispose to unmount all active disks.
/// </summary>
public sealed class MountManager : IDisposable
{
    private static readonly ILogger<MountManager> Logger = AppLog.CreateLogger<MountManager>();

    private readonly Dictionary<string, RamDisk> _disks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mount points currently being mounted by an in-flight <see cref="Mount"/> call, i.e.
    /// reserved but not yet in <see cref="_disks"/>. Closes the race where two concurrent
    /// <see cref="Mount"/> calls for the same mount point would otherwise both pass a
    /// point-in-time check against <see cref="_disks"/> and both proceed to actually mount a
    /// disk before either is registered.
    /// </summary>
    private readonly HashSet<string> _reservedMountPoints = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _syncRoot = new();

    /// <summary>
    /// How often <see cref="_activityPollTimer"/> checks for activity recorded by
    /// <see cref="OnDiskContentAccessed"/> and, if any, raises <see cref="ActivityDetected"/>.
    /// </summary>
    private static readonly TimeSpan ActivityPollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Set (lock-free) by <see cref="OnDiskContentAccessed"/>, which runs on WinFsp driver
    /// threads; consumed by <see cref="PollActivity"/>. <see cref="ActivityDetected"/> is raised
    /// from the poll timer rather than directly from <see cref="OnDiskContentAccessed"/> so a
    /// slow or throwing subscriber can never stall a WinFsp driver thread.
    /// </summary>
    private int _pendingRead;

    /// <summary>
    /// Set (lock-free) by <see cref="OnDiskContentAccessed"/>; consumed by
    /// <see cref="PollActivity"/>. See <see cref="_pendingRead"/>.
    /// </summary>
    private int _pendingWrite;

    /// <summary>
    /// Periodically drains <see cref="_pendingRead"/>/<see cref="_pendingWrite"/> and raises
    /// <see cref="ActivityDetected"/> from a timer thread.
    /// </summary>
    private readonly Timer _activityPollTimer;

    /// <summary>
    /// Raised at most once per <see cref="ActivityPollInterval"/> when any mounted disk's content
    /// was read or written since the last tick, with <c>true</c> if any of those accesses was a
    /// write and <c>false</c> if all were reads. Raised from a timer thread, never from a WinFsp
    /// driver thread.
    /// </summary>
    public event Action<bool>? ActivityDetected;

    /// <summary>
    /// Raised on the thread that called <see cref="Mount"/> after a disk is successfully mounted.
    /// </summary>
    public event EventHandler<RamDisk>? DiskMounted;

    /// <summary>
    /// Raised on the thread that called <see cref="Unmount"/> after a disk is unmounted.
    /// </summary>
    public event EventHandler<RamDisk>? DiskUnmounted;

    /// <summary>
    /// Creates a <see cref="MountManager"/> and starts its background activity-poll timer.
    /// </summary>
    public MountManager()
    {
        _activityPollTimer = new(_ => PollActivity(), null, ActivityPollInterval, ActivityPollInterval);
    }

    /// <summary>
    /// Unmounts and disposes all active disks.
    /// </summary>
    public void Dispose() => Dispose(null);

    /// <summary>
    /// Unmounts and disposes all active disks, as <see cref="Dispose()"/>, but reports save
    /// progress via <paramref name="onProgress"/>: the disk currently being saved, that disk's
    /// own save fraction in [0, 1], the overall fraction across the whole disposal (also [0, 1],
    /// averaged across all disks' own fractions), and that disk's total used bytes (captured
    /// before disposal, since <c>disk.UsedBytes</c> can no longer be read afterwards —
    /// <see cref="RamDisk.Dispose()"/> disposes the underlying node map). Disks are saved
    /// concurrently (bounded by half the processor count, since each disk's own save already
    /// parallelizes its Zstd compression internally), so <paramref name="onProgress"/> may be
    /// invoked from multiple threads at once — it must be thread-safe.
    /// </summary>
    /// <param name="onProgress">Optional progress callback.</param>
    public void Dispose(Action<RamDisk, double, double, ulong>? onProgress)
    {
        _activityPollTimer.Dispose();

        List<RamDisk> all;

        lock (_syncRoot)
        {
            all = [.. _disks.Values];
            _disks.Clear();
        }

        var count = all.Count;

        if (count == 0)
        {
            return;
        }

        // Captured once, before Dispose runs, since UsedBytes reads the node map that
        // Dispose() tears down — reading it from a progress callback fired after disposal
        // (e.g. the final 1.0 report below, or a delayed UI-thread dispatch of a mid-save
        // tick) would throw ObjectDisposedException.
        var totalBytesByDisk = new ulong[count];
        var progressByDisk = new double[count];
        var progressLock = new Lock();

        for (var i = 0; i < count; i++)
        {
            totalBytesByDisk[i] = all[i].UsedBytes;
            all[i].ContentAccessed -= OnDiskContentAccessed;
        }

        void ReportOverall(int diskIndex, double diskProgress)
        {
            if (onProgress is null)
            {
                return;
            }

            double overall;

            lock (progressLock)
            {
                progressByDisk[diskIndex] = diskProgress;
                overall = progressByDisk.Sum() / count;
            }

            onProgress(all[diskIndex], diskProgress, overall, totalBytesByDisk[diskIndex]);
        }

        // Each disk's own save already parallelizes its Zstd compression internally, so disks
        // are only saved a few at a time (not fully unbounded) to avoid oversubscribing the CPU.
        var parallelism = Math.Max(1, Environment.ProcessorCount / 2);

        Parallel.For(0, count, new() { MaxDegreeOfParallelism = parallelism }, i =>
        {
            ReportOverall(i, 0.0);

            var perDiskProgress = onProgress is null ? null : new Progress<double>(p => ReportOverall(i, p));
            all[i].Dispose(perDiskProgress);

            ReportOverall(i, 1.0);
        });
    }

    /// <summary>
    /// Returns a snapshot of all currently mounted disks.
    /// </summary>
    /// <returns>
    /// A read-only list of active <see cref="RamDisk"/> instances.
    /// </returns>
    public IReadOnlyList<RamDisk> GetAll()
    {
        lock (_syncRoot)
        {
            return new List<RamDisk>(_disks.Values).AsReadOnly();
        }
    }

    /// <summary>
    /// Creates and mounts a new RAM disk according to <paramref name="options"/>, then adds it
    /// to the managed collection.
    /// </summary>
    /// <param name="options">Mount configuration for the new disk.</param>
    /// <param name="password">
    /// Password to unlock <see cref="DiskOptions.PersistImagePath"/> if it points to an
    /// encrypted image.
    /// </param>
    /// <returns>
    /// The newly mounted <see cref="RamDisk"/>.
    /// </returns>
    /// <param name="progress">
    /// Optional progress reporter for the archive-extraction path
    /// (<see cref="DiskOptions.SourceArchivePath"/>), updated with a fraction in [0, 1].
    /// </param>
    /// <param name="cancellationToken">
    /// Optional token to cancel a slow archive extraction or image load; see
    /// <see cref="RamDisk.Create"/>. Cancellation can only happen before the disk is actually
    /// mounted, so nothing needs unmounting — the mount-point reservation is still released either
    /// way.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A disk is already registered at <see cref="DiskOptions.MountPoint"/>.
    /// </exception>
    public RamDisk Mount(
        DiskOptions options,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Reserve the mount point up front, under the lock, for the whole duration of the mount
        // (not just a point-in-time check) so a second concurrent Mount call for the same mount
        // point fails immediately instead of racing RamDisk.Create — which actually mounts a live
        // WinFsp volume — against this call.
        lock (_syncRoot)
        {
            if (_disks.ContainsKey(options.MountPoint) || !_reservedMountPoints.Add(options.MountPoint))
            {
                throw new InvalidOperationException($"A disk is already mounted at '{options.MountPoint}'.");
            }
        }

        RamDisk disk;
        try
        {
            disk = RamDisk.Create(options, password, progress, cancellationToken);
        }
        finally
        {
            lock (_syncRoot)
            {
                _reservedMountPoints.Remove(options.MountPoint);
            }
        }

        disk.ContentAccessed += OnDiskContentAccessed;

        lock (_syncRoot)
        {
            _disks[options.MountPoint] = disk;
        }

        DiskMounted?.Invoke(this, disk);
        return disk;
    }

    /// <summary>
    /// Unmounts and disposes the disk registered at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="mountPoint">The mount point string used when the disk was created.</param>
    /// <returns>
    /// <c>true</c> if a disk was found and unmounted; <c>false</c> if no disk was registered
    /// at that mount point.
    /// </returns>
    public bool Unmount(string mountPoint)
    {
        RamDisk? disk;

        lock (_syncRoot)
        {
            if (!_disks.Remove(mountPoint, out disk))
            {
                return false;
            }
        }

        disk.ContentAccessed -= OnDiskContentAccessed;
        disk.Dispose();
        DiskUnmounted?.Invoke(this, disk);
        return true;
    }

    /// <summary>
    /// Handler for every mounted disk's <see cref="RamDisk.ContentAccessed"/>. Runs on WinFsp
    /// driver threads, so it only sets a flag — see <see cref="_pendingRead"/>.
    /// </summary>
    private void OnDiskContentAccessed(bool isWrite)
    {
        if (isWrite)
        {
            Volatile.Write(ref _pendingWrite, 1);
        }
        else
        {
            Volatile.Write(ref _pendingRead, 1);
        }
    }

    /// <summary>
    /// Timer callback for <see cref="_activityPollTimer"/>: drains
    /// <see cref="_pendingRead"/>/<see cref="_pendingWrite"/> and raises
    /// <see cref="ActivityDetected"/> at most once per tick if either was set, preferring write.
    /// A subscriber exception is caught and logged rather than left to propagate out of this
    /// <see cref="Timer"/> callback, where it would otherwise be unhandled and crash the process.
    /// </summary>
    private void PollActivity()
    {
        var hadWrite = Interlocked.Exchange(ref _pendingWrite, 0) != 0;
        var hadRead = Interlocked.Exchange(ref _pendingRead, 0) != 0;

        if (!hadWrite && !hadRead)
        {
            return;
        }

        try
        {
            ActivityDetected?.Invoke(hadWrite);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ActivityDetected subscriber threw during activity poll.");
        }
    }
}
