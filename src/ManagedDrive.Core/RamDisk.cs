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
    private byte[]? _cek;
    private bool _disposed;
    private string? _lastSavedImagePath;
    private string? _password;

    private RamDisk(MemoryFileSystem fs, FileSystemHost host, DiskOptions options)
    {
        _fs = fs;
        _host = host;
        Options = options;
    }

    /// <summary>
    /// Occurs whenever an image save or snapshot write fails, whether triggered manually,
    /// by the periodic auto-save timer, or by the final save on unmount/dispose. The
    /// exception is also rethrown to the caller for saves that are awaited synchronously
    /// (e.g. a manual save); this event exists so background failures that would otherwise
    /// be swallowed (auto-save ticks, the final save in <see cref="Dispose"/>) can still be
    /// surfaced to the UI.
    /// </summary>
    public event EventHandler<Exception>? SaveFailed;

    /// <summary>
    /// Gets the password currently protecting this disk (if any), so a caller performing an
    /// in-process unmount/remount of the same disk (e.g. applying an edit that requires a full
    /// remount) can carry it forward to unlock the reloaded image without re-prompting the user.
    /// Never persisted; intended only for this kind of same-session hand-off.
    /// </summary>
    public string? CurrentPassword => _password;

    /// <summary>
    /// Gets the number of bytes currently available on this RAM disk.
    /// </summary>
    public ulong FreeBytes => TotalBytes > UsedBytes ? TotalBytes - UsedBytes : 0;

    /// <summary>
    /// Gets whether this disk's backing image is currently password-protected. The password
    /// itself is only held in memory for the lifetime of this instance and is never persisted.
    /// </summary>
    public bool IsPasswordProtected => _password is not null;

    /// <summary>
    /// Gets the UTC timestamp of the most recent content mutation (create/write/rename/delete/etc.),
    /// or <c>null</c> if the disk has never been modified since mount.
    /// </summary>
    public DateTimeOffset? LastContentWriteTime => _fs.LastContentWriteTimeUtc;

    /// <summary>
    /// Gets the UTC timestamp of the most recent successful image save (auto-save, final
    /// save on unmount, or manual save via <see cref="SaveToImage"/>). <c>null</c> if no
    /// save has occurred yet.
    /// </summary>
    public DateTimeOffset? LastSaveTime
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
    /// Gets the configured capacity (in bytes) that was in effect before <see cref="Create"/>
    /// auto-raised it to fit a loaded image whose actual content exceeded that capacity, or
    /// <c>null</c> if no such adjustment occurred at mount time. This is a one-time diagnostic
    /// snapshot taken during <see cref="Create"/> and does not change afterward; it is not
    /// persisted back to <see cref="Options"/> or any saved profile.
    /// </summary>
    public ulong? OriginalCapacityBytesOnLoad
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
    /// If <see cref="DiskOptions.SourceArchivePath"/> points to an existing archive file, it is
    /// extracted into the new disk (read-only). Otherwise, if
    /// <see cref="DiskOptions.PersistImagePath"/> points to an existing file, its contents are
    /// restored into the new disk. If the loaded content's actual size exceeds the capacity that
    /// would otherwise apply, the effective capacity is silently raised to fit the existing data
    /// (see <see cref="OriginalCapacityBytesOnLoad"/>) rather than failing the mount or leaving
    /// the disk permanently over capacity.
    /// </summary>
    /// <param name="options">Mount configuration.</param>
    /// <param name="password">
    /// Password to unlock <see cref="DiskOptions.PersistImagePath"/> if it points to an
    /// encrypted image. Ignored when the image is not encrypted or does not exist.
    /// </param>
    /// <returns>
    /// A fully mounted <see cref="RamDisk"/> instance.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when WinFsp returns a non-zero NTSTATUS from <c>Mount</c>.
    /// </exception>
    /// <exception cref="ImagePasswordRequiredException">
    /// Thrown when the image at <see cref="DiskOptions.PersistImagePath"/> is encrypted and
    /// <paramref name="password"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ImagePasswordIncorrectException">
    /// Thrown when <paramref name="password"/> does not match the one the image was encrypted with.
    /// </exception>
    public static RamDisk Create(DiskOptions options, string? password = null)
    {
        MemoryFileSystem fs;
        ulong? originalCapacity = null;
        byte[]? cek = null;

        if (options.SourceArchivePath != null &&
            File.Exists(options.SourceArchivePath))
        {
            var nodeMap = ArchiveNodeMapBuilder.BuildNodeMap(options.SourceArchivePath);

            var actualUsed = nodeMap.GetTotalAllocated();
            var capacity = ResolveEffectiveCapacity(options.CapacityBytes, actualUsed);
            if (capacity != options.CapacityBytes)
            {
                originalCapacity = options.CapacityBytes;
            }

            // Archive-sourced disks are always read-only: none of the supported archive
            // formats support writing changes back, regardless of what options.ReadOnly says.
            fs = new(capacity, options.VolumeLabel, nodeMap, readOnly: true);

            if (originalCapacity.HasValue)
            {
                options = options with
                {
                    CapacityBytes = capacity
                };
            }
        }
        else if (options.PersistImagePath != null &&
            File.Exists(options.PersistImagePath))
        {
            var nodeMap = DiskImageSerializer.Load(
                options.PersistImagePath,
                out var savedCapacity,
                out var savedLabel,
                password,
                out cek);

            var configuredCapacity = savedCapacity > 0 ? savedCapacity : options.CapacityBytes;
            var label = string.IsNullOrEmpty(savedLabel) ? options.VolumeLabel : savedLabel;

            var actualUsed = nodeMap.GetTotalAllocated();
            var capacity = ResolveEffectiveCapacity(configuredCapacity, actualUsed);
            if (capacity != configuredCapacity)
            {
                originalCapacity = configuredCapacity;
            }

            fs = new(capacity, label, nodeMap, options.ReadOnly);

            if (originalCapacity.HasValue)
            {
                options = options with
                {
                    CapacityBytes = capacity
                };
            }
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

        var disk = new RamDisk(fs, host, options)
        {
            OriginalCapacityBytesOnLoad = originalCapacity,
        };

        if (cek is not null)
        {
            // An existing encrypted image was loaded above: reuse its CEK as-is.
            disk._password = password;
            disk._cek = cek;
        }
        else if (password is not null)
        {
            // A brand-new disk (or one loaded from an unencrypted image) whose caller wants it
            // encrypted going forward: generate the CEK now, before the auto-save timer below can
            // possibly fire an unencrypted first save.
            disk.SetPassword(password);
        }

        disk.ConfigureAutoSaveTimer();
        return disk;
    }

    /// <summary>
    /// Unmounts the disk and releases all resources. After disposal the disk is no longer
    /// accessible from the Windows shell. If an image path is configured, a final save is
    /// performed before unmounting, unless nothing has changed since the last save.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = null;

            if (Options.PersistImagePath != null)
            {
                // Wait for any in-flight periodic save to finish, then perform the final save,
                // so the two never write to the image file concurrently.
                lock (_autoSaveLock)
                {
                    try
                    {
                        if (_fs.IsDirty || Options.PersistImagePath != _lastSavedImagePath)
                        {
                            SaveToImage();
                        }
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
    /// Writes this disk's current contents to a new image file at <paramref name="imagePath"/>.
    /// Unlike <see cref="SaveToImage"/>, this is independent of
    /// <see cref="DiskOptions.PersistImagePath"/> and does not affect this disk's dirty or
    /// last-saved-path tracking.
    /// </summary>
    /// <param name="imagePath">Destination file path.</param>
    /// <param name="level">Compression level applied to the exported image.</param>
    /// <param name="password">
    /// Password to protect the exported image with, or <see langword="null"/> to export
    /// unencrypted. Always uses a freshly generated content-encryption key independent of this
    /// disk's own <see cref="IsPasswordProtected"/> state, since the export is a standalone copy.
    /// </param>
    public void ExportToImage(string imagePath, ImageCompressionLevel level, string? password = null) =>
        DiskImageSerializer.Save(
            _fs.NodeMap,
            Options.CapacityBytes,
            Options.VolumeLabel,
            imagePath,
            level,
            password is not null ? new ImageEncryptionInfo(password, DiskImageSerializer.GenerateCek()) : null);

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

        try
        {
            DiskImageSerializer.Save(
                _fs.NodeMap,
                Options.CapacityBytes,
                Options.VolumeLabel,
                Options.PersistImagePath,
                Options.CompressionLevel,
                _password is not null && _cek is not null ? new ImageEncryptionInfo(_password, _cek) : null);
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke(this, ex);
            throw;
        }

        LastSaveTime = DateTimeOffset.UtcNow;
        _fs.ClearDirty();
        _lastSavedImagePath = Options.PersistImagePath;
    }

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
    /// Saves the disk image and, if snapshot retention is configured, writes a snapshot
    /// afterward. Coordinates with the periodic auto-save timer via <see cref="_autoSaveLock"/>
    /// so a manual save and a periodic auto-save never write/prune snapshots concurrently.
    /// </summary>
    public void SaveToImageWithSnapshot()
    {
        lock (_autoSaveLock)
        {
            SaveToImage();
            TryWriteSnapshot();
        }
    }

    /// <summary>
    /// Sets, changes, or removes this disk's password. Passing a non-null value when the disk is
    /// not yet encrypted generates a fresh content-encryption key (CEK); passing a non-null value
    /// when it is already encrypted only changes the password used to wrap the existing CEK, so
    /// previously written node data and snapshot blobs remain valid without re-encryption.
    /// Passing <see langword="null"/> removes password protection and discards the CEK — because
    /// historical snapshot blobs are encrypted with that CEK and cannot be recovered once it is
    /// discarded, this also deletes all of this disk's snapshots via
    /// <see cref="SnapshotManager.DeleteAllSnapshots"/>. Marks the disk dirty so the change takes
    /// effect on the next save.
    /// </summary>
    /// <param name="newPassword">The new password, or <see langword="null"/> to remove protection.</param>
    public void SetPassword(string? newPassword)
    {
        if (newPassword is not null)
        {
            _cek ??= DiskImageSerializer.GenerateCek();
            _password = newPassword;
        }
        else
        {
            _password = null;
            _cek = null;

            if (Options.PersistImagePath is { } path)
            {
                SnapshotManager.DeleteAllSnapshots(path);
            }
        }

        _fs.MarkDirty();
    }

    /// <summary>
    /// Applies <paramref name="newOptions"/> to the live disk without unmounting.
    /// Only <see cref="DiskOptions.VolumeLabel"/>, <see cref="DiskOptions.CapacityBytes"/>,
    /// <see cref="DiskOptions.AutoMount"/>, <see cref="DiskOptions.PersistImagePath"/>,
    /// <see cref="DiskOptions.AutoSaveIntervalMinutes"/>, <see cref="DiskOptions.CompressionLevel"/>,
    /// <see cref="DiskOptions.MaxSnapshotCount"/>, and <see cref="DiskOptions.MaxSnapshotSizeBytes"/>
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
    /// Replaces this disk's live contents with those stored in the snapshot at
    /// <paramref name="snapshotPath"/>. The replacement is marked dirty and will be persisted
    /// on the next save/auto-save tick; it is not written to
    /// <see cref="DiskOptions.PersistImagePath"/> immediately.
    /// </summary>
    /// <param name="snapshotPath">Path to the snapshot index file to restore from.</param>
    /// <param name="error">Set to a human-readable message when the method returns <c>false</c>.</param>
    /// <returns>
    /// <c>true</c> on success; <c>false</c> when the disk is read-only, the snapshot exceeds
    /// this disk's capacity, or the snapshot file could not be read.
    /// </returns>
    public bool TryRestoreFromSnapshot(string snapshotPath, out string? error)
    {
        lock (_autoSaveLock)
        {
            try
            {
                var nodeMap = SnapshotManager.LoadSnapshot(snapshotPath, out _, out _, _cek);
                return _fs.TryReplaceContents(nodeMap, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Determines the capacity a disk should mount with given its configured capacity and the
    /// actual bytes allocated by a loaded image, raising the configured value when the image's
    /// content would otherwise exceed it.
    /// </summary>
    internal static ulong ResolveEffectiveCapacity(ulong configuredCapacity, ulong actualUsed) =>
        Math.Max(configuredCapacity, actualUsed);

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
        host.VolumeCreationTime = (ulong)DateTimeOffset.UtcNow.ToFileTime();
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
    /// saving is still running, this tick is skipped instead of running concurrently with it.
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
                TryWriteSnapshot();
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

    /// <summary>
    /// Writes a timestamped snapshot copy of the just-saved image and prunes older snapshots
    /// per <see cref="DiskOptions.MaxSnapshotCount"/>/<see cref="DiskOptions.MaxSnapshotSizeBytes"/>.
    /// Does nothing if no image path is configured or neither snapshot limit is set. Must be
    /// called while <see cref="_autoSaveLock"/> is held.
    /// </summary>
    private void TryWriteSnapshot()
    {
        if (Options.PersistImagePath is not { } path)
        {
            return;
        }

        if (Options.MaxSnapshotCount is null && Options.MaxSnapshotSizeBytes is null)
        {
            return;
        }

        try
        {
            SnapshotManager.WriteSnapshot(
                _fs.NodeMap,
                Options.CapacityBytes,
                Options.VolumeLabel,
                path,
                DateTimeOffset.UtcNow,
                Options.CompressionLevel,
                _cek);

            SnapshotManager.Prune(path, Options.MaxSnapshotCount, Options.MaxSnapshotSizeBytes);
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke(this, ex);
            throw;
        }
    }
}