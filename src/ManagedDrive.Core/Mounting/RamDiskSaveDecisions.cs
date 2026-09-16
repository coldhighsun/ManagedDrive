namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Pure decision logic for whether <see cref="RamDisk"/> should save or write a snapshot,
/// extracted out of <see cref="RamDisk"/>'s private instance methods so it can be unit tested
/// directly (via explicit parameters) without going through <see cref="RamDisk.Create"/>'s real
/// WinFsp mount.
/// </summary>
internal static class RamDiskSaveDecisions
{
    /// <summary>
    /// Whether a save would actually write anything: the disk has unsaved changes, or the
    /// configured persist path has changed since the last successful save.
    /// </summary>
    internal static bool NeedsSave(bool isDirty, string? configuredPersistImagePath, string? lastSavedImagePath) =>
        isDirty || configuredPersistImagePath != lastSavedImagePath;

    /// <summary>
    /// Whether an exit/shutdown save should run: gated by <see cref="DiskOptions.SaveImageOnExit"/>
    /// on top of the shared <see cref="NeedsSave"/> condition.
    /// </summary>
    internal static bool NeedsExitSave(bool saveImageOnExit, bool needsSave) => saveImageOnExit && needsSave;

    /// <summary>
    /// Compares <paramref name="nodeMap"/> against the most recently written snapshot of
    /// <paramref name="mainImagePath"/>, if one exists. Returns <c>false</c> (i.e. "write a new
    /// snapshot") when there is no prior snapshot, or when reading/comparing it fails for any
    /// reason — a comparison failure should never silently suppress a snapshot.
    /// </summary>
    internal static bool IsUnchangedSinceLatestSnapshot(string mainImagePath, FileNodeMap nodeMap)
    {
        try
        {
            var snapshots = SnapshotManager.ListSnapshots(mainImagePath);
            if (snapshots.Count == 0)
            {
                return false;
            }

            var latest = snapshots[^1];
            return !SnapshotManager.DiffAgainstCurrent(latest.Path, nodeMap).HasChanges;
        }
        catch
        {
            return false;
        }
    }
}
