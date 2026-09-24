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
    /// Whether <see cref="RamDisk.SetPassword"/> should generate a fresh content-encryption key
    /// rather than reuse <paramref name="currentCek"/> to just re-wrap it under a new password.
    /// <see langword="true"/> only when there is no current CEK (the disk is unencrypted, or a
    /// password was removed since the last save) — in that case an existing on-disk image may
    /// still hold segments encrypted under a previous, now-discarded CEK, so the caller must also
    /// force a full rewrite on the next save rather than let segment reuse copy those stale-key
    /// segments verbatim under the new key.
    /// </summary>
    internal static bool ShouldGenerateNewCek(byte[]? currentCek) => currentCek is null;

    /// <summary>
    /// Whether the next save must rewrite the whole image rather than reuse segments of the file
    /// already at <paramref name="configuredPersistImagePath"/>. Each node's saved-segment
    /// bookkeeping describes the image it was last written to (or loaded from); if that isn't the
    /// file about to be written, the segment indices point into some other image's layout, and
    /// reusing them would copy stale or foreign bytes verbatim. Also forced after a CEK rotation,
    /// since reused segments would still be encrypted under the discarded key.
    /// </summary>
    internal static bool ShouldForceFullRewrite(
        bool cekRotatedSinceLastSave, string? configuredPersistImagePath, string? lastSavedImagePath) =>
        cekRotatedSinceLastSave || configuredPersistImagePath != lastSavedImagePath;

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
