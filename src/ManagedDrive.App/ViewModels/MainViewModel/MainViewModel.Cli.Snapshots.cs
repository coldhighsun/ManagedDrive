using ManagedDrive.Cli.Core;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Snapshot operations exposed to the <c>mdrive</c> CLI.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Writes a timestamped snapshot for the disk currently mounted at <paramref name="mountPoint"/>
    /// right now, for use by the CLI command channel. Mirrors <c>ExecuteCreateSnapshotNow</c>, via
    /// the same <see cref="RamDisk.SaveToImageWithSnapshot"/> call, but without the busy overlay.
    /// </summary>
    /// <param name="mountPoint">The mount point to snapshot, e.g. <c>"R:"</c>.</param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if no image path is configured
    /// or snapshot retention isn't configured (<see cref="DiskOptions.MaxSnapshotCount"/>/
    /// <see cref="DiskOptions.MaxSnapshotSizeBytes"/>); or <c>(false, string.Empty)</c> if no disk
    /// is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> CreateSnapshotByMountPointAsync(string mountPoint)
    {
        _logger.LogInformation("CLI snapshot create requested for {MountPoint}.", mountPoint);

        var vm = FindDisk(mountPoint);
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (!vm.HasImagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"));
        }

        if (!vm.SnapshotsEnabled)
        {
            return (false, Loc.Get("Msg.CliSnapshotsNotEnabled"));
        }

        try
        {
            await Task.Run(() => vm.Disk.SaveToImageWithSnapshot());
            _logger.LogInformation("CLI snapshot create completed for {MountPoint}.", mountPoint);
            return (true, Loc.Format("Status.ImageSaved", mountPoint));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI snapshot create failed for {MountPoint}.", mountPoint);
            return (false, Loc.Format("Msg.SaveImageFailed", ex.Message));
        }
    }

    /// <summary>
    /// Deletes a single snapshot of the disk currently mounted at <paramref name="mountPoint"/>,
    /// for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point whose snapshot should be deleted, e.g. <c>"R:"</c>.</param>
    /// <param name="index">
    /// 1-based snapshot index, newest first (1 = newest), re-resolved against the live snapshot
    /// directory at call time — it can refer to a different snapshot than the same index did in an
    /// earlier <see cref="ListSnapshotsByMountPointAsync"/> call if snapshots were added or removed
    /// in between (e.g. by another concurrent <c>mdrive</c> invocation).
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if no image path is configured,
    /// no snapshots exist, or <paramref name="index"/> is out of range; or
    /// <c>(false, string.Empty)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> DeleteSnapshotByMountPointAsync(string mountPoint, int index)
    {
        _logger.LogInformation("CLI delete snapshot requested for {MountPoint}, index {Index}.", mountPoint, index);

        var vm = FindDisk(mountPoint);
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"));
        }

        var ordered = await GetOrderedSnapshotsAsync(imagePath);
        if (index < 1 || index > ordered.Count)
        {
            return (false, Loc.Format("Msg.SnapshotIndexOutOfRange", ordered.Count));
        }

        var target = ordered[index - 1];
        await Task.Run(() => vm.Disk.DeleteSnapshot(target.Path));

        _logger.LogInformation("CLI delete snapshot completed for {MountPoint}.", mountPoint);
        return (true, Loc.Format("Status.SnapshotDeleted", mountPoint));
    }

    /// <summary>
    /// Lists the available snapshots of the disk currently mounted at <paramref name="mountPoint"/>,
    /// newest first, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to list snapshots for, e.g. <c>"R:"</c>.</param>
    /// <returns>
    /// <c>(true, string.Empty, snapshots)</c> on success (an empty list when none exist yet);
    /// <c>(false, message, null)</c> if no image path is configured; or
    /// <c>(false, string.Empty, null)</c> if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message, IReadOnlyList<CliSnapshotInfo>? Snapshots)> ListSnapshotsByMountPointAsync(string mountPoint)
    {
        _logger.LogInformation("CLI snapshot list requested for {MountPoint}.", mountPoint);

        var vm = FindDisk(mountPoint);
        if (vm == null)
        {
            return (false, string.Empty, null);
        }

        if (vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"), null);
        }

        var ordered = await GetOrderedSnapshotsAsync(imagePath);
        IReadOnlyList<CliSnapshotInfo> result = ordered
            .Select((s, i) => new CliSnapshotInfo(i + 1, s.TimestampUtc, (ulong)s.SizeBytes))
            .ToList();
        return (true, string.Empty, result);
    }

    /// <summary>
    /// Restores the disk currently mounted at <paramref name="mountPoint"/> from a previously
    /// saved snapshot, for use by the CLI command channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to restore, e.g. <c>"R:"</c>.</param>
    /// <param name="index">
    /// 1-based snapshot index, newest first (1 = newest), re-resolved against the live snapshot
    /// directory at call time — it can refer to a different snapshot than the same index did in an
    /// earlier <see cref="ListSnapshotsByMountPointAsync"/> call if snapshots were added or removed
    /// in between (e.g. by another concurrent <c>mdrive</c> invocation).
    /// </param>
    /// <returns>
    /// <c>(true, message)</c> on success; <c>(false, message)</c> if no image path is configured,
    /// no snapshots exist, <paramref name="index"/> is out of range, or the restore itself fails
    /// (e.g. read-only disk); or <c>(false, string.Empty)</c> if no disk is currently mounted at
    /// <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message)> RestoreSnapshotByMountPointAsync(string mountPoint, int index)
    {
        _logger.LogInformation("CLI restore snapshot requested for {MountPoint}, index {Index}.", mountPoint, index);

        var vm = FindDisk(mountPoint);
        if (vm == null)
        {
            return (false, string.Empty);
        }

        if (vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"));
        }

        var ordered = await GetOrderedSnapshotsAsync(imagePath);
        if (index < 1 || index > ordered.Count)
        {
            return (false, Loc.Format("Msg.SnapshotIndexOutOfRange", ordered.Count));
        }

        var target = ordered[index - 1];
        string? error = null;
        var success = await Task.Run(() => vm.Disk.TryRestoreFromSnapshot(target.Path, out error));

        if (!success)
        {
            _logger.LogWarning("CLI restore snapshot failed for {MountPoint}: {Error}", mountPoint, error);
            return (false, error ?? Loc.Get("Msg.RestoreSnapshotFailedUnknown"));
        }

        vm.Refresh();
        StatusText = Loc.Format("Status.SnapshotRestored", mountPoint);
        _logger.LogInformation("CLI restore snapshot completed for {MountPoint}.", mountPoint);
        return (true, StatusText);
    }

    /// <summary>
    /// Compares a previously saved snapshot of the disk currently mounted at
    /// <paramref name="mountPoint"/> against its current live contents, for use by the CLI command
    /// channel.
    /// </summary>
    /// <param name="mountPoint">The mount point to diff, e.g. <c>"R:"</c>.</param>
    /// <param name="index">
    /// 1-based snapshot index, newest first (1 = newest), re-resolved against the live snapshot
    /// directory at call time — it can refer to a different snapshot than the same index did in an
    /// earlier <see cref="ListSnapshotsByMountPointAsync"/> call if snapshots were added or removed
    /// in between (e.g. by another concurrent <c>mdrive</c> invocation).
    /// </param>
    /// <returns>
    /// <c>(true, string.Empty, diff)</c> on success; <c>(false, message, null)</c> if no image path
    /// is configured or <paramref name="index"/> is out of range; or <c>(false, string.Empty, null)</c>
    /// if no disk is currently mounted at <paramref name="mountPoint"/>.
    /// </returns>
    public async Task<(bool Success, string Message, SnapshotManager.SnapshotDiffResult? Diff)> DiffSnapshotByMountPointAsync(string mountPoint, int index)
    {
        _logger.LogInformation("CLI snapshot diff requested for {MountPoint}, index {Index}.", mountPoint, index);

        var vm = FindDisk(mountPoint);
        if (vm == null)
        {
            return (false, string.Empty, null);
        }

        if (vm.Disk.Options.PersistImagePath is not { } imagePath)
        {
            return (false, Loc.Get("Msg.SaveImageNoPath"), null);
        }

        var ordered = await GetOrderedSnapshotsAsync(imagePath);
        if (index < 1 || index > ordered.Count)
        {
            return (false, Loc.Format("Msg.SnapshotIndexOutOfRange", ordered.Count), null);
        }

        var target = ordered[index - 1];
        var diff = await Task.Run(() => vm.Disk.DiffAgainstSnapshot(target.Path));

        _logger.LogInformation("CLI snapshot diff completed for {MountPoint}.", mountPoint);
        return (true, string.Empty, diff);
    }

    /// <summary>
    /// Lists <paramref name="imagePath"/>'s snapshots newest first — the ordering that gives
    /// <see cref="ListSnapshotsByMountPointAsync"/>, <see cref="RestoreSnapshotByMountPointAsync"/>,
    /// and <see cref="DeleteSnapshotByMountPointAsync"/> their shared 1-based, "1 = newest" index.
    /// </summary>
    private static async Task<List<SnapshotManager.SnapshotInfo>> GetOrderedSnapshotsAsync(string imagePath) =>
        (await Task.Run(() => SnapshotManager.ListSnapshots(imagePath)))
        .OrderByDescending(s => s.TimestampUtc)
        .ToList();
}
