namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Manages the collection of active <see cref="RamDisk"/> instances.
/// Thread-safe; raises events when disks are mounted or unmounted.
/// Dispose to unmount all active disks.
/// </summary>
public sealed class MountManager : IDisposable
{
    private readonly Dictionary<string, RamDisk> _disks = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _syncRoot = new();

    /// <summary>
    /// Raised whenever any mounted disk's content is read or written, with <c>true</c> for
    /// writes and <c>false</c> for reads. Forwarded from each disk's
    /// <see cref="RamDisk.ContentAccessed"/>; fires on WinFsp driver threads, not the UI thread.
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
    /// Unmounts and disposes all active disks.
    /// </summary>
    public void Dispose() => Dispose(null);

    /// <summary>
    /// Unmounts and disposes all active disks, as <see cref="Dispose()"/>, but reports overall
    /// save progress across all disks via <paramref name="onOverallProgress"/> (the disk
    /// currently being saved, and a fraction in [0, 1] across the whole disposal).
    /// </summary>
    /// <param name="onOverallProgress">Optional overall progress callback.</param>
    public void Dispose(Action<RamDisk, double>? onOverallProgress)
    {
        List<RamDisk> all;

        lock (_syncRoot)
        {
            all = [.. _disks.Values];
            _disks.Clear();
        }

        var count = all.Count;

        for (var i = 0; i < count; i++)
        {
            var disk = all[i];
            var diskIndex = i;

            disk.ContentAccessed -= OnDiskContentAccessed;
            onOverallProgress?.Invoke(disk, (double)diskIndex / count);

            var perDiskProgress = onOverallProgress is null
                ? null
                : new Progress<double>(p => onOverallProgress(disk, (diskIndex + p) / count));
            disk.Dispose(perDiskProgress);

            onOverallProgress?.Invoke(disk, (double)(diskIndex + 1) / count);
        }
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
    public RamDisk Mount(DiskOptions options, string? password = null, IProgress<double>? progress = null)
    {
        var disk = RamDisk.Create(options, password, progress);
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

    private void OnDiskContentAccessed(bool isWrite) => ActivityDetected?.Invoke(isWrite);
}