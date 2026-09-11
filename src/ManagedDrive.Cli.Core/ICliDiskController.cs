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
    /// Returns a snapshot of all currently mounted disks.
    /// </summary>
    IReadOnlyList<CliDiskInfo> ListDisks();

    /// <summary>
    /// Mounts the contents of an archive file (zip, 7z, rar, tar, or any other format
    /// <c>SharpCompress</c> can read) at <paramref name="mountPoint"/> as a new read-only disk.
    /// </summary>
    /// <param name="archivePath">Path to an existing archive file.</param>
    /// <param name="mountPoint">
    /// The drive letter to mount at, or <c>null</c> to automatically pick the first free letter
    /// from <c>Z:</c> down to <c>D:</c>.
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
    /// <param name="mountPoint">The drive letter to mount at.</param>
    /// <param name="overrides">
    /// Per-field values the user explicitly passed via CLI flags; any <c>null</c> field defers to
    /// a saved profile for <paramref name="imagePath"/> if one exists, or the built-in default.
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> with a human-readable reason
    /// otherwise.
    /// </returns>
    Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides);

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