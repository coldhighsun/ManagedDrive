using Fsp;

namespace ManagedDrive.Core;

/// <summary>
/// Represents a single mounted RAM disk. Wraps a <see cref="MemoryFileSystem"/> and a
/// <see cref="FileSystemHost"/> and manages the mount/unmount lifecycle.
/// Dispose to unmount and free all resources.
/// </summary>
public sealed class RamDisk : IDisposable
{
    private const uint EventDriveAdd = 0x00000100;
    private const uint FlagFlush = 0x1000;
    private const uint FlagPath = 0x0005;
    private readonly Lock _autoSaveLock = new();
    private readonly MemoryFileSystem _fs;
    private readonly FileSystemHost _host;
    private Timer? _autoSaveTimer;
    private bool _disposed;
    private string? _lastSavedImagePath;

    private RamDisk(MemoryFileSystem fs, FileSystemHost host, DiskOptions options)
    {
        _fs = fs;
        _host = host;
        Options = options;
    }

    /// <summary>
    /// Gets the number of bytes currently available on this RAM disk.
    /// </summary>
    public ulong FreeBytes => TotalBytes > UsedBytes ? TotalBytes - UsedBytes : 0;

    /// <summary>
    /// Gets the UTC timestamp of the most recent content mutation (create/write/rename/delete/etc.),
    /// or <c>null</c> if the disk has never been modified since mount.
    /// </summary>
    public DateTime? LastContentWriteTime => _fs.LastContentWriteTimeUtc;

    /// <summary>
    /// Gets the UTC timestamp of the most recent successful image save (auto-save, final
    /// save on unmount, or manual save via <see cref="SaveToImage"/>). <c>null</c> if no
    /// save has occurred yet.
    /// </summary>
    public DateTime? LastSaveTime
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the mount point string as reported by WinFsp after a successful mount.
    /// </summary>
    public string MountPoint => _host.MountPoint() ?? Options.MountPoint;

    /// <summary>
    /// Gets the configuration used to create this disk.
    /// </summary>
    public DiskOptions Options
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the total configured capacity of this RAM disk in bytes.
    /// </summary>
    public ulong TotalBytes => Options.CapacityBytes;

    /// <summary>
    /// Gets the number of bytes currently allocated by files on this RAM disk.
    /// </summary>
    public ulong UsedBytes => _fs.NodeMap.GetTotalAllocated();

    /// <summary>
    /// Creates and mounts a new RAM disk according to the supplied options.
    /// If <see cref="DiskOptions.PersistImagePath"/> points to an existing file,
    /// its contents are restored into the new disk.
    /// </summary>
    /// <param name="options">Mount configuration.</param>
    /// <returns>
    /// A fully mounted <see cref="RamDisk"/> instance.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when WinFsp returns a non-zero NTSTATUS from <c>Mount</c>.
    /// </exception>
    public static RamDisk Create(DiskOptions options)
    {
        MemoryFileSystem fs;

        if (options.PersistImagePath != null &&
            File.Exists(options.PersistImagePath))
        {
            var nodeMap = DiskImageSerializer.Load(
                options.PersistImagePath,
                out var savedCapacity,
                out var savedLabel);

            var capacity = savedCapacity > 0 ? savedCapacity : options.CapacityBytes;
            var label = string.IsNullOrEmpty(savedLabel) ? options.VolumeLabel : savedLabel;
            fs = new(capacity, label, nodeMap, options.ReadOnly);
        }
        else
        {
            fs = new(options.CapacityBytes, options.VolumeLabel, options.ReadOnly);
        }

        var host = new FileSystemHost(fs);
        ConfigureHost(host);

        var status = host.Mount(options.MountPoint);
        if (status != FileSystemBase.STATUS_SUCCESS)
        {
            host.Dispose();
            throw new InvalidOperationException(
                $"WinFsp Mount failed for '{options.MountPoint}'. NTSTATUS: 0x{(uint)status:X8}");
        }

        // host.Mount with Synchronized=false starts the WinFsp dispatcher in a background
        // thread and returns immediately.  The drive letter is only visible in the OS once
        // that thread has completed the FspVolumeMount IOCTL.  Poll until it appears.
        if (IsDriveLetter(options.MountPoint) && !WaitForDriveVisible(options.MountPoint))
        {
            host.Dispose();
            throw new InvalidOperationException(
                $"WinFsp did not expose drive '{options.MountPoint}' within 2.5 s. " +
                "Verify that the WinFsp kernel driver is loaded and that the drive letter is not already in use.");
        }

        // Notify Windows Shell (Explorer) so the drive appears immediately.
        if (IsDriveLetter(options.MountPoint))
        {
            NotifyShellDriveAdded(options.MountPoint);
        }

        var disk = new RamDisk(fs, host, options);
        disk.ConfigureAutoSaveTimer();
        return disk;
    }

    /// <summary>
    /// Unmounts the disk and releases all resources. After disposal the disk is no longer
    /// accessible from the Windows shell. If auto-save is enabled, a final save is performed
    /// before unmounting.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = null;

            if (Options.AutoSaveIntervalMinutes is > 0)
            {
                // Wait for any in-flight periodic save to finish, then perform the final save,
                // so the two never write to the image file concurrently.
                lock (_autoSaveLock)
                {
                    try
                    {
                        SaveToImage();
                    }
                    catch
                    {
                        // Best-effort final save.
                    }
                }
            }

            _host.Unmount();
            _host.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Removes all files and directories from the disk, leaving it empty.
    /// Does nothing and returns <c>false</c> when the disk is read-only.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the format succeeded; <c>false</c> if the disk is read-only.
    /// </returns>
    public bool Format()
    {
        if (Options.ReadOnly)
        {
            return false;
        }

        _fs.NodeMap.ClearAll();
        _fs.MarkDirty();
        return true;
    }

    /// <summary>
    /// Serializes the current disk contents to <see cref="DiskOptions.PersistImagePath"/>.
    /// Does nothing if <see cref="DiskOptions.PersistImagePath"/> is <c>null</c>.
    /// </summary>
    public void SaveToImage()
    {
        if (Options.PersistImagePath == null)
        {
            return;
        }

        DiskImageSerializer.Save(
            _fs.NodeMap,
            Options.CapacityBytes,
            Options.VolumeLabel,
            Options.PersistImagePath,
            Options.CompressionLevel);

        LastSaveTime = DateTime.UtcNow;
        _fs.ClearDirty();
        _lastSavedImagePath = Options.PersistImagePath;
    }

    /// <summary>
    /// Replaces this disk's entire contents with a copy of <paramref name="source"/>'s current
    /// contents. Existing files on this disk are discarded.
    /// </summary>
    /// <param name="source">The disk to copy content from.</param>
    /// <param name="error">Set to a human-readable message when the method returns <c>false</c>.</param>
    /// <returns>
    /// <c>true</c> on success; <c>false</c> when this disk is read-only or its capacity is
    /// smaller than the source disk's used bytes.
    /// </returns>
    public bool TryCloneFrom(RamDisk source, out string? error) =>
        _fs.TryReplaceContents(source._fs.NodeMap, out error);

    /// <summary>
    /// Writes this disk's current contents to a new image file at <paramref name="imagePath"/>.
    /// Unlike <see cref="SaveToImage"/>, this is independent of
    /// <see cref="DiskOptions.PersistImagePath"/> and does not affect this disk's dirty or
    /// last-saved-path tracking.
    /// </summary>
    /// <param name="imagePath">Destination file path.</param>
    /// <param name="level">Compression level applied to the exported image.</param>
    public void ExportToImage(string imagePath, ImageCompressionLevel level) =>
        DiskImageSerializer.Save(_fs.NodeMap, Options.CapacityBytes, Options.VolumeLabel, imagePath, level);

    /// <summary>
    /// Saves the disk image while holding <see cref="_autoSaveLock"/>, without unmounting.
    /// Intended for external shutdown-notification callers (e.g. Windows session-ending) that
    /// need a quick, safe save that can't race the periodic auto-save tick. Does nothing if
    /// <see cref="DiskOptions.PersistImagePath"/> is <c>null</c>.
    /// </summary>
    public void SaveToImageSafe()
    {
        lock (_autoSaveLock)
        {
            SaveToImage();
        }
    }

    /// <summary>
    /// Applies <paramref name="newOptions"/> to the live disk without unmounting.
    /// Only <see cref="DiskOptions.VolumeLabel"/>, <see cref="DiskOptions.CapacityBytes"/>,
    /// <see cref="DiskOptions.AutoMount"/>, <see cref="DiskOptions.PersistImagePath"/>,
    /// <see cref="DiskOptions.AutoSaveIntervalMinutes"/>, and <see cref="DiskOptions.CompressionLevel"/>
    /// may be changed this way.
    /// <see cref="DiskOptions.MountPoint"/> and <see cref="DiskOptions.ReadOnly"/> require a
    /// full unmount/remount.
    /// </summary>
    /// <param name="newOptions">The updated options to apply.</param>
    /// <param name="error">
    /// Set to a human-readable message when the method returns <c>false</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> on success; <c>false</c> when the capacity reduction is rejected because
    /// current usage exceeds <paramref name="newOptions"/>.<see cref="DiskOptions.CapacityBytes"/>.
    /// </returns>
    public bool TryApplyOptions(DiskOptions newOptions, out string? error)
    {
        if (newOptions.CapacityBytes != Options.CapacityBytes &&
            !_fs.TryUpdateCapacity(newOptions.CapacityBytes))
        {
            error = $"Cannot reduce capacity: current usage ({UsedBytes:N0} bytes) exceeds the requested capacity ({newOptions.CapacityBytes:N0} bytes).";
            return false;
        }

        if (newOptions.VolumeLabel != Options.VolumeLabel)
        {
            _fs.UpdateVolumeLabel(newOptions.VolumeLabel);
        }

        Options = newOptions;
        ConfigureAutoSaveTimer();
        error = null;
        return true;
    }

    private static void ConfigureHost(FileSystemHost host)
    {
        host.SectorSize = (ushort)FileNode.AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileSystemName = "NTFS";
        host.CasePreservedNames = true;
        host.CaseSensitiveSearch = false;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.FileInfoTimeout = 1000;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)new Random().Next(int.MaxValue / 2);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="mountPoint"/> is a Windows drive-letter path
    /// of the form <c>X:</c> (single letter followed by a colon).
    /// </summary>
    private static bool IsDriveLetter(string mountPoint) =>
        mountPoint.Length == 2 && char.IsLetter(mountPoint[0]) && mountPoint[1] == ':';

    /// <summary>
    /// Broadcasts a <c>EventDriveAdd</c> Shell change notification so that Windows Explorer
    /// immediately refreshes and shows the newly mounted drive letter.
    /// </summary>
    /// <param name="mountPoint">Drive-letter mount point in the form <c>X:</c>.</param>
    private static void NotifyShellDriveAdded(string mountPoint)
    {
        // Shell expects the path to end with a backslash.
        var path = mountPoint.TrimEnd('\\') + '\\';
        SHChangeNotify(EventDriveAdd, FlagPath | FlagFlush, path, null);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string? dwItem1, string? dwItem2);

    /// <summary>
    /// Polls <see cref="System.IO.DriveInfo.GetDrives"/> until the drive letter described by
    /// <paramref name="mountPoint"/> is reported by the OS, or a 2.5-second timeout elapses.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the drive became visible; <c>false</c> when the timeout expired.
    /// </returns>
    private static bool WaitForDriveVisible(string mountPoint)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            Thread.Sleep(100);

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (string.Equals(
                    drive.Name.TrimEnd('\\'),
                    mountPoint,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// (Re)starts the periodic auto-save timer based on the current <see cref="Options"/>,
    /// or stops it when auto-save is disabled or no image path is configured. The first save
    /// fires immediately (on a background thread) so that enabling auto-save via create/edit
    /// captures the current contents right away instead of waiting a full interval.
    /// </summary>
    private void ConfigureAutoSaveTimer()
    {
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = null;

        if (Options.AutoSaveIntervalMinutes is { } minutes and > 0 &&
            Options.PersistImagePath != null)
        {
            var interval = TimeSpan.FromMinutes(minutes);
            _autoSaveTimer = new(_ => TryAutoSave(), null, TimeSpan.Zero, interval);
        }
    }

    /// <summary>
    /// Saves the disk image on the periodic timer tick, swallowing any exception so a failed
    /// save does not affect the mounted disk or crash the timer thread. If the previous tick's
    /// save is still running, this tick is skipped instead of running concurrently with it.
    /// Skips the save entirely (no disk I/O) when the disk's content has not changed since the
    /// last successful save and the configured image path hasn't changed either.
    /// </summary>
    private void TryAutoSave()
    {
        if (!_autoSaveLock.TryEnter())
        {
            return;
        }

        try
        {
            if (_fs.IsDirty || Options.PersistImagePath != _lastSavedImagePath)
            {
                SaveToImage();
            }
        }
        catch
        {
            // Best-effort periodic save.
        }
        finally
        {
            _autoSaveLock.Exit();
        }
    }
}