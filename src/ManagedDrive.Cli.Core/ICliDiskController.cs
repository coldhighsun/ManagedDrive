namespace ManagedDrive.Cli.Core;

/// <summary>
/// Everything <see cref="CliCommandProcessor"/> needs from the host application to execute CLI
/// subcommands, without depending on the WPF app layer directly (which would create a circular
/// project reference, since the app layer is what hosts the CLI pipe server).
/// </summary>
public interface ICliDiskController
{
    /// <summary>
    /// Formats the disk currently mounted at <paramref name="mountPoint"/>, permanently deleting
    /// all files and folders on it.
    /// </summary>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise. <paramref name="mountPoint"/> not being mounted is reported as
    /// <c>(false, string.Empty)</c> so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message)> FormatAsync(string mountPoint);

    /// <summary>
    /// Exports the disk currently mounted at <paramref name="mountPoint"/> to a standalone file —
    /// either a plain <c>.mdr</c> image (<paramref name="archiveFormat"/> is <c>null</c>) or an
    /// archive (<paramref name="archiveFormat"/> is set) — without touching the disk's own
    /// persistence configuration.
    /// </summary>
    /// <param name="mountPoint">The mount point to export, e.g. <c>"R:"</c>.</param>
    /// <param name="outputPath">Destination file path to write the export to.</param>
    /// <param name="archiveFormat"><c>null</c> to export a <c>.mdr</c> image; otherwise the archive container format.</param>
    /// <param name="compressionLevel">Compression level applied to the export.</param>
    /// <param name="password">
    /// Password to encrypt the exported <c>.mdr</c> image with, or <c>null</c> for no encryption.
    /// Ignored when <paramref name="archiveFormat"/> is set, since archive formats don't support it.
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise. <paramref name="mountPoint"/> not being mounted is reported as
    /// <c>(false, string.Empty)</c> so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message)> ExportAsync(
        string mountPoint, string outputPath, ArchiveExportFormat? archiveFormat, ImageCompressionLevel compressionLevel, string? password);

    /// <summary>
    /// Returns a snapshot of all currently mounted disks.
    /// </summary>
    IReadOnlyList<CliDiskInfo> ListDisks();

    /// <summary>
    /// Mounts the contents of an archive file (zip, 7z, rar, tar, or any other format
    /// <c>SharpCompress</c> can read) at <paramref name="mountPoint"/> as a new read-only disk.
    /// </summary>
    /// <param name="archivePath">Path to an existing archive file.</param>
    /// <param name="mountPoint">
    /// The drive letter to mount at, the path of an existing empty directory, or <c>null</c> to
    /// automatically pick the first free letter from <c>Z:</c> down to <c>D:</c>.
    /// </param>
    /// <param name="overrides">
    /// Per-field values the user explicitly passed via CLI flags; only <see cref="CliMountOverrides.AutoMount"/>
    /// applies to an archive-sourced disk. Any other field is ignored, since an archive-sourced
    /// disk is always read-only and has no backing image to configure persistence for.
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including when <paramref name="mountPoint"/> is <c>null</c> and no drive letter
    /// is free.
    /// </returns>
    Task<(bool Success, string Message)> MountArchiveAsync(string archivePath, string? mountPoint, CliMountOverrides overrides);

    /// <summary>
    /// Mounts an existing disk image at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="imagePath">Path to an existing <c>.mdr</c> disk image.</param>
    /// <param name="mountPoint">The drive letter to mount at, or the path of an existing empty directory.</param>
    /// <param name="overrides">
    /// Per-field values the user explicitly passed via CLI flags; any <c>null</c> field defers to
    /// a saved profile for <paramref name="imagePath"/> if one exists, or the built-in default.
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including a mount point directory that doesn't exist or isn't empty.
    /// </returns>
    Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides);

    /// <summary>
    /// Creates a brand-new, empty RAM disk at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="mountPoint">The drive letter to mount at, e.g. <c>"R:"</c>.</param>
    /// <param name="capacityBytes">The disk's capacity in bytes.</param>
    /// <param name="volumeLabel">The volume label, or <c>null</c> to use the built-in default.</param>
    /// <param name="imagePath">
    /// Optional path to persist the disk to (created on first save); <c>null</c> for a
    /// memory-only disk that is discarded on unmount.
    /// </param>
    /// <param name="password">Optional password to encrypt <paramref name="imagePath"/> with.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including a mount point already in use, an invalid capacity, or an image path
    /// collision.
    /// </returns>
    Task<(bool Success, string Message)> CreateAsync(string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password);

    /// <summary>
    /// Lists the immediate children of <paramref name="path"/> on the disk currently mounted at
    /// <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="mountPoint">The mount point to list, e.g. <c>"R:"</c>.</param>
    /// <param name="path">
    /// The directory to list, e.g. <c>"\Folder"</c>; <c>null</c> or empty lists the root.
    /// </param>
    /// <returns>
    /// <c>(true, message, entries)</c> on success (an empty list when the directory has no
    /// children); <c>(false, message, null)</c> with a human-readable reason otherwise —
    /// including a <paramref name="path"/> that doesn't exist or names a file.
    /// <paramref name="mountPoint"/> not being mounted is reported as <c>(false, string.Empty, null)</c>
    /// so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message, IReadOnlyList<CliFileEntry>? Entries)> ListFilesAsync(string mountPoint, string? path);

    /// <summary>
    /// Replaces the contents of the disk mounted at <paramref name="targetMountPoint"/> with a
    /// copy of the disk mounted at <paramref name="sourceMountPoint"/>'s current contents.
    /// </summary>
    /// <param name="sourceMountPoint">The mount point to copy content from, e.g. <c>"R:"</c>.</param>
    /// <param name="targetMountPoint">The mount point to overwrite, e.g. <c>"S:"</c>.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including either mount point not being mounted, the target being read-only, or
    /// the target's capacity being smaller than the source's used bytes.
    /// </returns>
    Task<(bool Success, string Message)> CloneAsync(string sourceMountPoint, string targetMountPoint);

    /// <summary>
    /// Writes a timestamped snapshot for the disk currently mounted at <paramref name="mountPoint"/>
    /// right now, independent of any regular image save.
    /// </summary>
    /// <param name="mountPoint">The mount point to snapshot, e.g. <c>"R:"</c>.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including no image path being configured, or snapshot retention not being
    /// configured. <paramref name="mountPoint"/> not being mounted is reported as
    /// <c>(false, string.Empty)</c> so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message)> CreateSnapshotAsync(string mountPoint);

    /// <summary>
    /// Deletes a single snapshot of the disk currently mounted at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="mountPoint">The mount point whose snapshot should be deleted, e.g. <c>"R:"</c>.</param>
    /// <param name="index">
    /// 1-based snapshot index as returned by <see cref="ListSnapshotsAsync"/> (1 = newest).
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including an out-of-range <paramref name="index"/>.
    /// <paramref name="mountPoint"/> not being mounted is reported as <c>(false, string.Empty)</c>
    /// so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message)> DeleteSnapshotAsync(string mountPoint, int index);

    /// <summary>
    /// Lists the available snapshots of the disk currently mounted at <paramref name="mountPoint"/>,
    /// newest first (<see cref="CliSnapshotInfo.Index"/> 1 = newest).
    /// </summary>
    /// <returns>
    /// <c>(true, message, snapshots)</c> on success (an empty list when none exist yet);
    /// <c>(false, message, null)</c> with a human-readable reason otherwise.
    /// <paramref name="mountPoint"/> not being mounted is reported as <c>(false, string.Empty, null)</c>
    /// so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsAsync(string mountPoint);

    /// <summary>
    /// Restores the disk currently mounted at <paramref name="mountPoint"/> from a previously
    /// saved snapshot, replacing its current contents.
    /// </summary>
    /// <param name="mountPoint">The mount point to restore, e.g. <c>"R:"</c>.</param>
    /// <param name="index">
    /// 1-based snapshot index as returned by <see cref="ListSnapshotsAsync"/> (1 = newest).
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise — including a read-only disk, a capacity mismatch, or an out-of-range
    /// <paramref name="index"/>. <paramref name="mountPoint"/> not being mounted is reported as
    /// <c>(false, string.Empty)</c> so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message)> RestoreSnapshotAsync(string mountPoint, int index);

    /// <summary>
    /// Requests that the running ManagedDrive application exit. Must not block until the process
    /// has actually shut down — the actual exit should happen after this call returns (e.g. on a
    /// short delay), so the CLI response reporting success can still be written back over the
    /// pipe before the host process starts tearing down its own <c>CliPipeServer</c>.
    /// </summary>
    Task RequestExitAsync();

    /// <summary>
    /// Saves the disk currently mounted at <paramref name="mountPoint"/> to its backing image
    /// file immediately.
    /// </summary>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise. <paramref name="mountPoint"/> not being mounted is reported as
    /// <c>(false, string.Empty)</c> so the CLI layer can render its own not-mounted message.
    /// </returns>
    Task<(bool Success, string Message)> SaveAsync(string mountPoint);

    /// <summary>
    /// Sets or removes the encryption password of the disk currently mounted at
    /// <paramref name="mountPoint"/>. Takes effect on the next save.
    /// </summary>
    /// <param name="mountPoint">The mount point to change, e.g. <c>"R:"</c>.</param>
    /// <param name="newPassword">The new password, or <see langword="null"/> to remove protection.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, string.Empty)</c> if no disk is currently
    /// mounted at <paramref name="mountPoint"/>.
    /// </returns>
    Task<(bool Success, string Message)> SetPasswordAsync(string mountPoint, string? newPassword);

    /// <summary>
    /// Unmounts the disk currently mounted at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="mountPoint">The mount point to unmount (e.g. <c>"R:"</c>).</param>
    /// <param name="deleteImage">
    /// If <c>true</c>, also deletes the disk's backing image file (and any snapshots) or source
    /// archive file after unmounting.
    /// </param>
    /// <returns>
    /// <c>true</c> if a mounted disk was found and unmounted; <c>false</c> if no disk is
    /// currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    Task<bool> UnmountAsync(string mountPoint, bool deleteImage);
}

/// <summary>
/// Read-only snapshot of a mounted disk, as needed to render the CLI <c>list</c> table.
/// </summary>
public sealed record CliDiskInfo(string MountPoint, string VolumeLabel, ulong UsedBytes, ulong TotalBytes);

/// <summary>
/// One entry of a disk's snapshot history, as needed to render the CLI <c>snapshot list</c>
/// table and to address a specific snapshot in <c>snapshot restore</c>/<c>snapshot delete</c>.
/// </summary>
/// <param name="Index">1-based index, newest first (1 = newest).</param>
public sealed record CliSnapshotInfo(int Index, DateTimeOffset TimestampUtc, ulong SizeBytes);

/// <summary>
/// One entry of a directory listing, as needed to render the CLI <c>ls</c> table.
/// </summary>
public sealed record CliFileEntry(string Name, bool IsDirectory, ulong SizeBytes);
