using Fsp;
using Fsp.Interop;
using System.Runtime.InteropServices;
using ThrottledLogging;

namespace ManagedDrive.Core.Mounting;

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
    private static readonly ILogger<RamDisk> Logger = AppLog.CreateLogger<RamDisk>();

    /// <summary>
    /// Source of unique, monotonically increasing <see cref="_instanceId"/> values, used to give
    /// every <see cref="RamDisk"/> a stable lock-acquisition order.
    /// </summary>
    private static long _nextInstanceId;

    private readonly Lock _autoSaveLock = new();
    private readonly MemoryFileSystem _fs;
    private readonly FileSystemHost _host;

    /// <summary>
    /// Unique id assigned at construction, used only to order <see cref="_autoSaveLock"/>
    /// acquisition across two disks (see <see cref="TryCloneFrom"/>) so that cloning in opposite
    /// directions between the same two disks concurrently can't deadlock.
    /// </summary>
    private readonly long _instanceId = Interlocked.Increment(ref _nextInstanceId);

    private Timer? _autoSaveTimer;
    private byte[]? _cek;
    private int _disposed;
    private string? _lastSavedImagePath;
    private string? _password;

    /// <summary>
    /// Set when <see cref="SetPassword"/> generates a brand-new content-encryption key while an
    /// existing on-disk image may still be encrypted under a previous key (removing and then
    /// re-adding a password before an intervening save). Consumed by the next <see cref="SaveToImage"/>
    /// to force a full rewrite instead of segment reuse — see <see cref="DiskImageSerializer.SaveIncremental"/>.
    /// </summary>
    private bool _cekRotatedSinceLastSave;

    private RamDisk(MemoryFileSystem fs, FileSystemHost host, DiskOptions options)
    {
        _fs = fs;
        _host = host;
        Options = options;
        _fs.ContentAccessed += OnContentAccessed;
    }

    /// <summary>
    /// Raised whenever this disk's content is read or written, with <c>true</c> for writes and
    /// <c>false</c> for reads. Forwarded from the underlying <see cref="MemoryFileSystem"/>;
    /// fires on WinFsp driver threads, not the UI thread.
    /// </summary>
    public event Action<bool>? ContentAccessed;

    /// <summary>
    /// Raised whenever an image save or snapshot write fails, whether triggered manually,
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
    /// Gets the path of the file most recently read, or <c>null</c> if the disk has never been
    /// read from since mount. Best-effort, for UI display only.
    /// </summary>
    public string? LastContentReadPath => _fs.LastContentReadPath;

    /// <summary>
    /// Gets the path of the file most recently written, or <c>null</c> if the disk has never had
    /// content written to it since mount. Best-effort, for UI display only.
    /// </summary>
    public string? LastContentWritePath => _fs.LastContentWritePath;

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
    /// Gets the cumulative number of bytes read from file content since mount. Never resets;
    /// consumers derive a rate by sampling the delta over time (see <see cref="ThroughputTracker"/>).
    /// </summary>
    public long TotalBytesRead => _fs.TotalBytesRead;

    /// <summary>
    /// Gets the cumulative number of bytes written to file content since mount. Never resets;
    /// consumers derive a rate by sampling the delta over time (see <see cref="ThroughputTracker"/>).
    /// </summary>
    public long TotalBytesWritten => _fs.TotalBytesWritten;

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
    /// <param name="progress">
    /// Optional progress reporter, updated with a fraction in [0, 1]. For the archive-extraction
    /// path (<see cref="DiskOptions.SourceArchivePath"/>) this is a byte-weighted fraction of
    /// archive content extracted; for the image-load path (<see cref="DiskOptions.PersistImagePath"/>)
    /// it is the fraction of the image file's raw bytes read so far (see
    /// <see cref="DiskImageSerializer.Load"/>). Ignored when neither path applies (a brand-new disk).
    /// </param>
    /// <param name="cancellationToken">
    /// Optional token to cancel a slow archive extraction or image load. Checked each time
    /// <paramref name="progress"/> would be reported, so an unmodified disk or one whose loaded
    /// content is one huge node/file only notices cancellation at the next report, not
    /// mid-node/file. Ignored once mounting has actually started (WinFsp's own <c>Mount</c> call
    /// and the wait for the drive letter to appear are not cancellable).
    /// </param>
    public static RamDisk Create(
        DiskOptions options,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        MemoryFileSystem fs;
        ulong? originalCapacity = null;
        byte[]? cek = null;
        var loadedFromPersistImagePath = false;
        var cancellableProgress = CancellableProgress.Wrap(progress, cancellationToken);

        if (options.SourceArchivePath != null &&
            File.Exists(options.SourceArchivePath))
        {
            ArchiveNodeMapBuilder.PeekArchive(options.SourceArchivePath, out var totalBytes, out _);
            var nodeMap = ArchiveNodeMapBuilder.BuildNodeMap(options.SourceArchivePath, (long)totalBytes, cancellableProgress);

            var capacity = ResolveAndApplyCapacity(nodeMap, options.CapacityBytes, ref options, out originalCapacity);

            // Archive-sourced disks are always read-only: none of the supported archive
            // formats support writing changes back, regardless of what options.ReadOnly says.
            fs = new(capacity, options.VolumeLabel, nodeMap, readOnly: true);
        }
        else if (options.PersistImagePath != null &&
            File.Exists(options.PersistImagePath))
        {
            loadedFromPersistImagePath = true;
            var nodeMap = DiskImageSerializer.Load(
                options.PersistImagePath,
                out var savedCapacity,
                out var savedLabel,
                password,
                out cek,
                cancellableProgress);

            var configuredCapacity = savedCapacity > 0 ? savedCapacity : options.CapacityBytes;
            var label = string.IsNullOrEmpty(savedLabel) ? options.VolumeLabel : savedLabel;

            var capacity = ResolveAndApplyCapacity(nodeMap, configuredCapacity, ref options, out originalCapacity);

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
            DisposeFailedMount(host, fs, cek);
            throw new InvalidOperationException(
                $"WinFsp Mount failed for '{options.MountPoint}'. NTSTATUS: 0x{(uint)status:X8}");
        }

        // host.Mount with Synchronized=false starts the WinFsp dispatcher in a background
        // thread and returns immediately.  The drive letter is only visible in the OS once
        // that thread has completed the FspVolumeMount IOCTL.  Poll until it appears.
        if (IsDriveLetter(options.MountPoint) && !WaitForDriveVisible(options.MountPoint))
        {
            DisposeFailedMount(host, fs, cek);
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

        if (loadedFromPersistImagePath)
        {
            // Loaded content matches what's already on disk at this path, so an unmodified
            // disk shouldn't be considered "unsaved" just because this instance hasn't itself
            // called SaveToImage yet. Archive-sourced disks don't qualify even if
            // PersistImagePath happens to already exist, since their content came from the
            // archive, not from that file.
            disk._lastSavedImagePath = options.PersistImagePath;
        }

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
            try
            {
                disk.SetPassword(password);
            }
            catch
            {
                // The WinFsp volume is already mounted and disk owns host/fs at this point, so a
                // failure here (e.g. the RNG call inside CEK generation) must tear both down the
                // same way DisposeFailedMount does for the earlier failure branches above —
                // otherwise the drive letter stays mounted forever with no RamDisk ever registered
                // in MountManager._disks to unmount it through.
                disk.Dispose();
                throw;
            }
        }

        disk.ConfigureAutoSaveTimer();
        return disk;
    }

    /// <summary>
    /// Releases the host, node map, and content-encryption key created so far by
    /// <see cref="Create"/> when mounting fails after <paramref name="host"/> was constructed,
    /// before a <see cref="RamDisk"/> instance exists to own them.
    /// </summary>
    /// <param name="host">The WinFsp host to dispose.</param>
    /// <param name="fs">The file system whose node map should be disposed.</param>
    /// <param name="cek">The loaded content-encryption key to zero, if any.</param>
    private static void DisposeFailedMount(FileSystemHost host, MemoryFileSystem fs, byte[]? cek)
    {
        host.Dispose();
        fs.NodeMap.Dispose();
        if (cek is not null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(cek);
        }
    }

    /// <summary>
    /// Compares the snapshot at <paramref name="snapshotPath"/> against this disk's current live
    /// contents, without restoring anything or reading any blob content beyond the snapshot's
    /// stored per-file SHA-256 hashes.
    /// </summary>
    public SnapshotManager.SnapshotDiffResult DiffAgainstSnapshot(string snapshotPath)
    {
        lock (_autoSaveLock)
        {
            return SnapshotManager.DiffAgainstCurrent(snapshotPath, _fs.NodeMap);
        }
    }

    /// <summary>
    /// Deletes the snapshot at <paramref name="snapshotPath"/> and garbage-collects any blobs it
    /// left as the only referent, under <see cref="_autoSaveLock"/> so this can't race a concurrent
    /// <see cref="TryWriteSnapshot"/> (periodic auto-save or a manual <see cref="SaveToImageWithSnapshot"/>)
    /// on this same disk — without that, the blob GC could delete a blob a write in flight had
    /// already committed to the shared blob directory but whose owning snapshot index file hadn't
    /// yet been renamed into place, leaving that snapshot referencing a blob that no longer exists.
    /// </summary>
    public void DeleteSnapshot(string snapshotPath)
    {
        lock (_autoSaveLock)
        {
            SnapshotManager.DeleteSnapshot(Options.PersistImagePath!, snapshotPath);
        }
    }

    /// <summary>
    /// Unmounts the disk and releases all resources. After disposal the disk is no longer
    /// accessible from the Windows shell. If an image path is configured, a final save is
    /// performed once unmounted (so no late write is missed), unless nothing has changed since the last save.
    /// </summary>
    public void Dispose() => Dispose(null);

    /// <summary>
    /// Unmounts the disk and releases all resources, as <see cref="Dispose()"/>, but reports
    /// progress of the final save (if one is performed) via <paramref name="progress"/>.
    /// </summary>
    /// <param name="progress">Optional progress reporter, updated with a fraction in [0, 1].</param>
    public void Dispose(IProgress<double>? progress)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            // Already disposed (or a concurrent Dispose is in flight) — nothing more to do.
            return;
        }

        try
        {
            _fs.ContentAccessed -= OnContentAccessed;
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = null;

            try
            {
                // Unmount before the final save, not after: while the volume is still mounted,
                // WinFsp callbacks can keep writing after the save has captured the tree, and
                // those writes would be silently lost once the node map is disposed below.
                //
                // Deliberately not under _autoSaveLock: unmounting can block for as long as WinFsp
                // takes to drain outstanding I/O (or an external process to release a handle on the
                // volume), and holding the lock across that would stall every other _autoSaveLock-
                // guarded operation on this disk (SetPassword, TryApplyOptions, a manual save, ...)
                // for the same duration. Nothing here touches _fs.NodeMap or _cek/_password, so a
                // SaveToImage() call that's still running (or starts) while this is in flight is
                // safe — the save and cleanup below, which do touch them, wait for the same lock.
                _host.Unmount();
                _host.Dispose();
            }
            finally
            {
                // Runs even if unmounting threw, so a failed unmount doesn't also cost the user
                // their unsaved changes.
                SaveOnDispose(progress);
            }
        }
        finally
        {
            // Locked so a SaveToImage() call that acquired _autoSaveLock before this point (and
            // is still running) fully finishes — using the still-valid NodeMap/CEK — before this
            // disposes/zeroes them; and so a SaveToImage() call that hasn't acquired the lock yet
            // is guaranteed to see _disposed already set (it was set at the very top of this
            // method, before any of this) once it does, and rejects itself instead of proceeding.
            lock (_autoSaveLock)
            {
                // Again under the lock: a TryApplyOptions call that passed its disposed check
                // just before _disposed was set may have re-armed the timer after it was disposed
                // at the top of this method.
                _autoSaveTimer?.Dispose();
                _autoSaveTimer = null;

                // The host is unmounted, so no WinFsp callbacks can still touch the map. Runs even
                // if the final save or the unmount above threw, so the node map's lock and the CEK
                // are always released rather than leaked.
                _fs.NodeMap.Dispose();

                if (_cek is not null)
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(_cek);
                    _cek = null;
                }

                _password = null;
            }
        }
    }

    /// <summary>
    /// Performs <see cref="Dispose(IProgress{double}?)"/>'s final save, if an image path is
    /// configured and <see cref="NeedsExitSave"/> says one is needed. Waits for any in-flight
    /// periodic save (or a <see cref="SaveToImage"/> call that read <see cref="_disposed"/> as 0
    /// just before Dispose set it) to finish first, so two saves never write to the image file at
    /// the same time. Best-effort: a failure is logged, never thrown out of Dispose.
    /// </summary>
    /// <param name="progress">Optional progress reporter, updated with a fraction in [0, 1].</param>
    private void SaveOnDispose(IProgress<double>? progress)
    {
        if (Options.PersistImagePath == null)
        {
            return;
        }

        lock (_autoSaveLock)
        {
            try
            {
                if (NeedsExitSave())
                {
                    SaveToImageCore(progress, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                // SaveToImageCore already raised SaveFailed before rethrowing, so UI subscribers
                // are notified.
                Logger.LogWarning(ex, "Final save to '{ImagePath}' failed during Dispose", Options.PersistImagePath);
            }
        }
    }

    /// <summary>
    /// Writes this disk's current contents to a new zip or 7z archive file at
    /// <paramref name="archivePath"/>. Unlike <see cref="ExportToImage"/> the result is a plain
    /// archive readable by any zip/7z tool, not a <c>.mdr</c> image — it cannot be re-mounted
    /// directly, only re-imported via <see cref="ArchiveNodeMapBuilder"/>.
    /// </summary>
    /// <param name="archivePath">Destination archive file path.</param>
    /// <param name="format">The archive container format to write.</param>
    /// <param name="level">Compression level applied to the archive.</param>
    /// <param name="progress">Optional progress reporter, updated with a fraction in [0, 1].</param>
    /// <param name="cancellationToken">
    /// Optional token to cancel the export. Checked each time <paramref name="progress"/> would be
    /// reported (per node written), so a disk with one huge file as a single node only notices
    /// cancellation once that file finishes writing, not mid-file. <paramref name="archivePath"/>
    /// itself is never left partially written — <see cref="ArchiveNodeMapWriter.WriteArchive"/>
    /// writes to a temp file and only renames it to <paramref name="archivePath"/> on success,
    /// deleting the temp file on any failure including cancellation.
    /// </param>
    public void ExportToArchive(
        string archivePath,
        ArchiveExportFormat format,
        ImageCompressionLevel level,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        ArchiveNodeMapWriter.WriteArchive(_fs.NodeMap, archivePath, format, level, CancellableProgress.Wrap(progress, cancellationToken));

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
    /// <param name="progress">Optional progress reporter, updated with a fraction in [0, 1].</param>
    /// <param name="cancellationToken">
    /// Optional token to cancel the export. Checked each time <paramref name="progress"/> would be
    /// reported (per node written). <paramref name="imagePath"/> itself is never left partially
    /// written — <see cref="DiskImageSerializer.Save"/> writes to a temp file and only renames it
    /// to <paramref name="imagePath"/> on success, deleting the temp file on any failure including
    /// cancellation.
    /// </param>
    public void ExportToImage(
        string imagePath,
        ImageCompressionLevel level,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        DiskImageSerializer.Save(
            _fs.NodeMap,
            Options.CapacityBytes,
            Options.VolumeLabel,
            imagePath,
            level,
            password is not null ? new ImageEncryptionInfo(password, DiskImageSerializer.GenerateCek()) : null,
            CancellableProgress.Wrap(progress, cancellationToken));

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

        lock (_autoSaveLock)
        {
            ThrowIfDisposed();
            _fs.NodeMap.ClearAll();
            _fs.MarkDirty();
        }

        NotifyVolumeContentsChanged();
        return true;
    }

    /// <summary>
    /// Tells WinFsp the whole tree under the volume root may have changed, so any Explorer window
    /// (or other listener) watching a directory on this disk re-enumerates instead of showing stale
    /// cached entries. Used after bulk content replacement (<see cref="Format"/>,
    /// <see cref="TryCloneFrom"/>, <see cref="TryRestoreFromSnapshot"/>) that mutates
    /// <see cref="MemoryFileSystem.NodeMap"/> directly rather than through individual WinFsp
    /// callbacks, which is what normally drives per-file notifications. Best-effort: a failure here
    /// (e.g. the host isn't running) is not surfaced to the caller.
    /// </summary>
    private void NotifyVolumeContentsChanged()
    {
        try
        {
            _host.Notify([
                new()
                {
                    FileName = "\\",
                    Action = NotifyAction.Modified,
                    Filter = NotifyFilter.ChangeFileName | NotifyFilter.ChangeDirName |
                             NotifyFilter.ChangeSize | NotifyFilter.ChangeLastWrite,
                },
            ]);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to notify WinFsp of a bulk content change on {MountPoint}.", MountPoint);
        }
    }

    /// <summary>
    /// Returns a snapshot of every file and directory node currently on this disk, keyed by full
    /// path. Read-only: intended for UI views (e.g. a disk content browser) that display the
    /// disk's contents without mutating it.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, FileNode>> GetAllNodes() => _fs.NodeMap.GetAllNodes();

    /// <summary>
    /// Serializes the current disk contents to <see cref="DiskOptions.PersistImagePath"/>.
    /// Does nothing if <see cref="DiskOptions.PersistImagePath"/> is <c>null</c>.
    /// </summary>
    /// <param name="progress">Optional progress reporter, updated with a fraction in [0, 1].</param>
    /// <param name="cancellationToken">
    /// Optional token to cancel the save. Checked each time <paramref name="progress"/> would be
    /// reported (per node/segment written). A cancellation leaves the disk exactly as dirty as it
    /// was before the save started — see <see cref="MemoryFileSystem.ClearDirtySince"/> — so a
    /// later save (manual or the next auto-save tick) picks up all of the disk's contents rather
    /// than just what changed after the cancellation. Unlike a genuine save failure, a cancellation
    /// does not raise <see cref="SaveFailed"/> or log an error.
    /// </param>
    public void SaveToImage(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RamDisk));
        }

        // Serializes with the periodic auto-save tick and with Dispose's own final save and
        // NodeMap/CEK cleanup (all of which take this lock too — System.Threading.Lock supports
        // recursive re-entry by the owning thread, so Dispose's direct call to SaveToImageCore is
        // unaffected) so two saves never write to the image file at the same time.
        lock (_autoSaveLock)
        {
            // Re-checked now that the lock is held: the check above can race a concurrent
            // Dispose() that hadn't set _disposed yet when it ran. Dispose sets _disposed before
            // doing anything else, then takes this same lock for its final save and again for its
            // NodeMap/CEK cleanup (though not for the unmount in between, which doesn't touch
            // either) — so by the time this thread gets the lock, either Dispose hasn't started
            // (safe to proceed) or _disposed is already set (must bail out here instead of
            // touching state Dispose is about to, or already did, tear down).
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(RamDisk));
            }

            SaveToImageCore(progress, cancellationToken);
        }
    }

    /// <summary>
    /// The actual save logic behind <see cref="SaveToImage"/>, factored out so <see cref="Dispose(IProgress{double}?)"/>
    /// can perform its own final save without going through <see cref="SaveToImage"/>'s disposed
    /// check — by the time Dispose reaches its final save, <see cref="_disposed"/> has already
    /// been set. Callers must already hold <see cref="_autoSaveLock"/>.
    /// </summary>
    private void SaveToImageCore(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Options.PersistImagePath == null)
        {
            return;
        }

        var versionAtSaveStart = _fs.CaptureMutationVersion();
        var forceFullRewrite = RamDiskSaveDecisions.ShouldForceFullRewrite(
            _cekRotatedSinceLastSave, Options.PersistImagePath, _lastSavedImagePath);

        try
        {
            DiskImageSerializer.SaveIncremental(
                _fs.NodeMap,
                Options.CapacityBytes,
                Options.VolumeLabel,
                Options.PersistImagePath,
                Options.CompressionLevel,
                _password is not null && _cek is not null ? new ImageEncryptionInfo(_password, _cek) : null,
                CancellableProgress.Wrap(progress, cancellationToken),
                Options.CustomZstdLevel,
                forceFullRewrite);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogErrorThrottled(
                $"save-failed:{Options.PersistImagePath}", TimeSpan.FromMinutes(10),
                "Failed to save disk image to {ImagePath}: {Error}", Options.PersistImagePath, ex.Message);
            SaveFailed?.Invoke(this, ex);
            throw;
        }

        _cekRotatedSinceLastSave = false;
        LastSaveTime = DateTimeOffset.UtcNow;
        _fs.ClearDirtySince(versionAtSaveStart);
        _lastSavedImagePath = Options.PersistImagePath;
        Logger.LogInformation("Saved disk image to {ImagePath}.", Options.PersistImagePath);
    }

    /// <summary>
    /// Saves the disk image while holding <see cref="_autoSaveLock"/>, without unmounting.
    /// Intended for external shutdown-notification callers (e.g. Windows session-ending) that
    /// need a quick, safe save that can't race the periodic auto-save tick. Does nothing when
    /// <see cref="NeedsExitSave"/> is <c>false</c> — i.e. save-on-exit is disabled for this disk
    /// (<see cref="DiskOptions.SaveImageOnExit"/>), or the disk's content hasn't changed since the
    /// last successful save and the configured image path hasn't changed either. This keeps
    /// opted-out or unmodified disks out of the OS shutdown time budget.
    /// </summary>
    public void SaveToImageSafe()
    {
        lock (_autoSaveLock)
        {
            if (!NeedsExitSave())
            {
                return;
            }

            try
            {
                SaveToImage();
            }
            catch
            {
                // Best-effort: SaveToImage already logs the failure and raises SaveFailed.
            }
        }
    }

    /// <summary>
    /// Saves the disk image and, if snapshot retention is configured, writes a snapshot
    /// afterward. Coordinates with the periodic auto-save timer via <see cref="_autoSaveLock"/>
    /// so a manual save and a periodic auto-save never write/prune snapshots concurrently.
    /// </summary>
    /// <param name="progress">
    /// Optional progress reporter, updated with a fraction in [0, 1]. When snapshot retention is
    /// configured (see <see cref="DiskOptions.MaxSnapshotCount"/>/<see cref="DiskOptions.MaxSnapshotSizeBytes"/>)
    /// and a snapshot write may actually happen, this range is split across both the image save
    /// (the first half) and the snapshot write (the second half); otherwise the full range is
    /// given to the image save alone, so the bar doesn't stall at 50% when no snapshot work
    /// follows it.
    /// </param>
    /// <param name="cancellationToken">
    /// Optional token to cancel either the image save or the snapshot write. See
    /// <see cref="SaveToImage"/> for how cancellation interacts with the dirty flag; a snapshot
    /// write canceled mid-way leaves no partial snapshot registered (nothing is added to the
    /// snapshot index until the write completes).
    /// </param>
    public void SaveToImageWithSnapshot(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_autoSaveLock)
        {
            // MappedProgress forwards straight into the caller's IProgress<double> instead of
            // wrapping it in a new Progress<double> constructed on this background thread — that
            // would capture a non-UI SynchronizationContext and route every tick through an extra,
            // unordered ThreadPool hop before it ever reached the UI-bound progress object.
            var mayWriteSnapshot = Options.PersistImagePath is not null
                && (Options.MaxSnapshotCount is not null || Options.MaxSnapshotSizeBytes is not null);

            if (mayWriteSnapshot)
            {
                SaveToImage(progress is null ? null : new MappedProgress(progress, 0.5, 0.0), cancellationToken);
                TryWriteSnapshot(progress is null ? null : new MappedProgress(progress, 0.5, 0.5), cancellationToken);
            }
            else
            {
                SaveToImage(progress, cancellationToken);
                TryWriteSnapshot(cancellationToken: cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(1.0);
        }
    }

    /// <summary>
    /// Sets, changes, or removes this disk's password. Passing a non-null value when the disk is
    /// not yet encrypted generates a fresh content-encryption key (CEK) and deletes all of this
    /// disk's existing snapshots, which were written in plaintext and would otherwise stay readable
    /// on disk after the user asked for encryption; passing a non-null value
    /// when it is already encrypted only changes the password used to wrap the existing CEK, so
    /// previously written node data and snapshot blobs remain valid without re-encryption.
    /// Passing <see langword="null"/> removes password protection and discards the CEK — because
    /// historical snapshot blobs are encrypted with that CEK and cannot be recovered once it is
    /// discarded, this also deletes all of this disk's snapshots via
    /// <see cref="SnapshotManager.DeleteAllSnapshots"/>. Marks the disk dirty so the change takes
    /// effect on the next save.
    /// </summary>
    /// <param name="newPassword">The new password, or <see langword="null"/> to remove protection.</param>
    /// <exception cref="IOException">
    /// Thrown when encrypting a previously unencrypted disk and any of its plaintext snapshots
    /// cannot be deleted; the disk is left unencrypted.
    /// </exception>
    public void SetPassword(string? newPassword)
    {
        lock (_autoSaveLock)
        {
            // Without this, a call that lost the race with Dispose would generate a CEK nothing
            // ever zeroes, or delete every snapshot of a disk that is no longer mounted.
            ThrowIfDisposed();

            if (newPassword is not null)
            {
                if (RamDiskSaveDecisions.ShouldGenerateNewCek(_cek))
                {
                    // Nothing is committed until the plaintext snapshots are gone: if deleting
                    // them fails, a CEK assigned first would leave the disk holding a key but no
                    // password, so later saves stay plaintext while new snapshots are encrypted
                    // under a key nothing wraps, and a retry would skip the deletion entirely.
                    // DeleteAllSnapshots only logs individual failures, so a leftover (e.g. a
                    // locked index file) must be turned into an error here; otherwise the disk
                    // would report itself encrypted while plaintext snapshot data stays on disk.
                    var newCek = DiskImageSerializer.GenerateCek();
                    try
                    {
                        if (Options.PersistImagePath is { } plaintextPath &&
                            !SnapshotManager.DeleteAllSnapshots(plaintextPath))
                        {
                            throw new IOException(
                                $"Could not delete the existing unencrypted snapshots of '{plaintextPath}'; the password was not set.");
                        }
                    }
                    catch
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(newCek);
                        throw;
                    }

                    _cek = newCek;
                    _cekRotatedSinceLastSave = true;
                }

                _password = newPassword;
            }
            else
            {
                if (_cek is not null)
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(_cek);
                }

                _password = null;
                _cek = null;

                if (Options.PersistImagePath is { } path)
                {
                    SnapshotManager.DeleteAllSnapshots(path);
                }
            }

            _fs.MarkDirty();
        }
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
        lock (_autoSaveLock)
        {
            // Without this, a call that lost the race with Dispose would re-arm the auto-save
            // timer on a disk that is already gone.
            if (IsDisposed)
            {
                error = DisposedError;
                return false;
            }

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
    public bool TryCloneFrom(RamDisk source, out string? error)
    {
        // Locks both disks' _autoSaveLock, in a stable order by _instanceId, so source can't be
        // concurrently mutated by its own auto-save/format/etc. while its node map is copied, and
        // so a clone running the other way between the same two disks at the same time can't
        // deadlock on the two locks.
        var (first, second) = _instanceId < source._instanceId ? (this, source) : (source, this);

        lock (first._autoSaveLock)
        {
            lock (second._autoSaveLock)
            {
                if (IsDisposed || source.IsDisposed)
                {
                    error = DisposedError;
                    return false;
                }

                if (!_fs.TryReplaceContents(source._fs.NodeMap, out error))
                {
                    return false;
                }
            }
        }

        NotifyVolumeContentsChanged();
        return true;
    }

    /// <summary>
    /// Resolves the underlying NT device path (e.g. <c>\Device\Volume{GUID}</c>) that this disk's
    /// drive-letter mount point currently maps to, via <c>QueryDosDevice</c>. Read-only and
    /// non-privileged, but it must be called from the user's own session — the drive-letter
    /// symlink WinFsp created lives in that session's DOS-device namespace, so a SYSTEM process
    /// could not resolve it. The result is handed to the helper service to publish a global
    /// symlink.
    /// </summary>
    /// <param name="devicePath">
    /// On success, the resolved NT device path; otherwise <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the mount point is a drive letter that resolved to a device path;
    /// <c>false</c> otherwise.
    /// </returns>
    public bool TryGetVolumeDevicePath(out string? devicePath)
    {
        devicePath = null;

        var mountPoint = MountPoint;
        if (!IsDriveLetter(mountPoint))
        {
            return false;
        }

        var buffer = new char[1024];
        var length = QueryDosDevice(mountPoint, buffer, (uint)buffer.Length);
        if (length == 0)
        {
            return false;
        }

        // QueryDosDevice returns a double-null-terminated list; the first entry is the target.
        devicePath = new string(buffer, 0, (int)length).Split('\0', 2)[0];
        return devicePath.Length > 0;
    }

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
            if (IsDisposed)
            {
                error = DisposedError;
                return false;
            }

            try
            {
                // The loaded map is ours alone, so its nodes are adopted rather than copied again.
                var nodeMap = SnapshotManager.LoadSnapshot(snapshotPath, out _, out _, _cek);
                if (!_fs.TryReplaceContents(nodeMap, out error, adoptNodes: true))
                {
                    return false;
                }

                NotifyVolumeContentsChanged();
                return true;
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

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> once <see cref="Dispose(IProgress{double}?)"/>
    /// has started. Callers check this while holding <see cref="_autoSaveLock"/>, so an operation
    /// either runs entirely before Dispose's final save and cleanup or is rejected here.
    /// </summary>
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    /// <summary>
    /// Whether <see cref="Dispose(IProgress{double}?)"/> has started. The <c>Try*</c> operations
    /// check this under <see cref="_autoSaveLock"/> and report <see cref="DisposedError"/> instead
    /// of throwing, keeping their "return false with an error" contract.
    /// </summary>
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Error reported by the <c>Try*</c> operations when the disk was disposed before they ran.
    /// </summary>
    private const string DisposedError = "The disk is no longer mounted.";

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
        // Only invoke the Cleanup callback when a file was actually modified. Read-only opens (the
        // common read-after-write and random-read path) then skip the user-mode Cleanup round-trip
        // entirely, shrinking WinFsp's per-handle overhead. Matches the official WinFsp memfs sample.
        host.PostCleanupWhenModifiedOnly = true;
        host.VolumeCreationTime = (ulong)DateTimeOffset.UtcNow.ToFileTime();
        host.VolumeSerialNumber = (uint)Random.Shared.Next(int.MaxValue / 2);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="mountPoint"/> is a Windows drive-letter path
    /// of the form <c>X:</c> (single letter followed by a colon).
    /// </summary>
    private static bool IsDriveLetter(string mountPoint) => MountPointValidator.IsDriveLetter(mountPoint);

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, uint max);

    /// <summary>
    /// Resolves the effective capacity for a just-loaded <paramref name="nodeMap"/> against
    /// <paramref name="configuredCapacity"/> via <see cref="ResolveEffectiveCapacity"/>, and — if
    /// that raised the capacity — updates <paramref name="options"/> in place to the new value and
    /// reports the original in <paramref name="originalCapacity"/>. Shared by <see cref="Create"/>'s
    /// archive-import and image-load branches.
    /// </summary>
    private static ulong ResolveAndApplyCapacity(
        FileNodeMap nodeMap, ulong configuredCapacity, ref DiskOptions options, out ulong? originalCapacity)
    {
        var actualUsed = nodeMap.GetTotalAllocated();
        var capacity = ResolveEffectiveCapacity(configuredCapacity, actualUsed);
        originalCapacity = capacity != configuredCapacity ? configuredCapacity : null;

        if (originalCapacity.HasValue)
        {
            options = options with
            {
                CapacityBytes = capacity
            };
        }

        return capacity;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
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
    /// Compares the disk's current contents against the most recently written snapshot of
    /// <paramref name="mainImagePath"/>, if one exists. See
    /// <see cref="RamDiskSaveDecisions.IsUnchangedSinceLatestSnapshot"/> for the decision logic.
    /// </summary>
    private bool IsUnchangedSinceLatestSnapshot(string mainImagePath) =>
        RamDiskSaveDecisions.IsUnchangedSinceLatestSnapshot(mainImagePath, _fs.NodeMap);

    /// <summary>
    /// Whether an exit/shutdown save should run. Used by <see cref="Dispose"/> and
    /// <see cref="SaveToImageSafe"/> so a disk with save-on-exit disabled is left untouched when
    /// the app exits or Windows shuts down, while periodic auto-save is unaffected. See
    /// <see cref="RamDiskSaveDecisions.NeedsExitSave"/> for the decision logic.
    /// </summary>
    private bool NeedsExitSave() => RamDiskSaveDecisions.NeedsExitSave(Options.SaveImageOnExit, NeedsSave());

    /// <summary>
    /// Whether a save would actually write anything. Shared by <see cref="SaveToImageSafe"/> and
    /// <see cref="TryAutoSave"/> so both skip saving under the same condition. See
    /// <see cref="RamDiskSaveDecisions.NeedsSave"/> for the decision logic.
    /// </summary>
    private bool NeedsSave() => RamDiskSaveDecisions.NeedsSave(_fs.IsDirty, Options.PersistImagePath, _lastSavedImagePath);

    private void OnContentAccessed(bool isWrite) => ContentAccessed?.Invoke(isWrite);

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
            if (NeedsSave())
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
    /// Does nothing if no image path is configured, neither snapshot limit is set, or the disk's
    /// contents are identical to the most recent existing snapshot (checked cheaply via stored
    /// SHA-256 hashes, without reading any blob) — this keeps a disk that gets saved repeatedly
    /// without real content changes (e.g. every mount's immediate first auto-save tick) from
    /// accumulating redundant, identical snapshots. Must be called while
    /// <see cref="_autoSaveLock"/> is held.
    /// </summary>
    private void TryWriteSnapshot(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Options.PersistImagePath is not { } path)
        {
            progress?.Report(1.0);
            return;
        }

        if (Options.MaxSnapshotCount is null && Options.MaxSnapshotSizeBytes is null)
        {
            progress?.Report(1.0);
            return;
        }

        if (IsUnchangedSinceLatestSnapshot(path))
        {
            progress?.Report(1.0);
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
                _cek,
                CancellableProgress.Wrap(progress, cancellationToken),
                Options.CustomZstdLevel);

            SnapshotManager.Prune(path, Options.MaxSnapshotCount, Options.MaxSnapshotSizeBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke(this, ex);
            throw;
        }
    }

    private sealed class MappedProgress(IProgress<double> inner, double scale, double offset) : IProgress<double>
    {
        public void Report(double value) => inner.Report(offset + (value * scale));
    }
}
