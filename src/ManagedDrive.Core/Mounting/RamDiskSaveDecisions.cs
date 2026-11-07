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
    /// Whether disposing the disk may write an image at all: a persist path is configured and the
    /// final save was not switched off.
    /// </summary>
    internal static bool HasFinalSaveTarget(string? persistImagePath, bool skipFinalSave) =>
        persistImagePath != null && !skipFinalSave;

    /// <summary>
    /// Whether disposing the disk now would write its image: <see cref="HasFinalSaveTarget"/> and
    /// <see cref="NeedsExitSave"/> both hold. This is what <c>RamDisk.SaveOnDispose</c> decides.
    /// </summary>
    internal static bool WillSaveOnDispose(string? persistImagePath, bool skipFinalSave, bool saveImageOnExit, bool needsSave) =>
        HasFinalSaveTarget(persistImagePath, skipFinalSave) && NeedsExitSave(saveImageOnExit, needsSave);

    /// <summary>
    /// The image files a format deletes: the configured path and, when it differs from the file the
    /// disk last saved to (the path was changed after the last save), that older file too, because
    /// it still holds the disk's previous contents. The older file is left alone when another
    /// mounted disk uses it. Paths are compared by full path, ignoring case.
    /// </summary>
    /// <param name="configuredPersistImagePath">The disk's configured image path, if any.</param>
    /// <param name="lastSavedImagePath">The file the disk last saved to, if any.</param>
    /// <param name="imagePathsOfOtherDisks">The image paths of the other mounted disks.</param>
    /// <returns>Empty when the disk has no image path; otherwise the configured path first.</returns>
    internal static IReadOnlyList<string> ImagePathsToClean(
        string? configuredPersistImagePath,
        string? lastSavedImagePath,
        IEnumerable<string>? imagePathsOfOtherDisks)
    {
        if (configuredPersistImagePath is null)
        {
            return [];
        }

        if (lastSavedImagePath is null ||
            FilePathComparison.AreSame(lastSavedImagePath, configuredPersistImagePath) ||
            (imagePathsOfOtherDisks?.Any(other => FilePathComparison.AreSame(other, lastSavedImagePath)) ?? false))
        {
            return [configuredPersistImagePath];
        }

        return [configuredPersistImagePath, lastSavedImagePath];
    }

    /// <summary>
    /// The image files a format may actually delete: <see cref="ImagePathsToClean"/> limited to
    /// the ones the caller confirmed. A change since the confirmation (a disk unmounted, the path
    /// edited) can therefore never get a file deleted that the user was not shown.
    /// </summary>
    /// <param name="configuredPersistImagePath">The disk's configured image path, if any.</param>
    /// <param name="lastSavedImagePath">The file the disk last saved to, if any.</param>
    /// <param name="imagePathsOfOtherDisks">The image paths that belong to something else.</param>
    /// <param name="confirmedImagePaths">The files the user was told would be deleted.</param>
    internal static IReadOnlyList<string> ConfirmedImagePathsToClean(
        string? configuredPersistImagePath,
        string? lastSavedImagePath,
        IEnumerable<string>? imagePathsOfOtherDisks,
        IReadOnlyCollection<string> confirmedImagePaths) =>
        [.. ImagePathsToClean(configuredPersistImagePath, lastSavedImagePath, imagePathsOfOtherDisks)
            .Where(path => confirmedImagePaths.Any(confirmed => FilePathComparison.AreSame(confirmed, path)))];

    /// <summary>
    /// Whether a format must stop before touching anything: the disk's configured image path has
    /// a file that the caller did not confirm for deletion (the path was edited after the
    /// confirmation), and writing the formatted disk there would overwrite it.
    /// </summary>
    /// <param name="configuredPersistImagePath">The disk's configured image path, if any.</param>
    /// <param name="configuredFileExists">Whether a file exists at that path.</param>
    /// <param name="confirmedImagePaths">The files the user was told would be deleted.</param>
    internal static bool ConfiguredImageIsUnconfirmed(
        string? configuredPersistImagePath,
        bool configuredFileExists,
        IReadOnlyCollection<string> confirmedImagePaths) =>
        configuredPersistImagePath is not null &&
        configuredFileExists &&
        !confirmedImagePaths.Any(confirmed => FilePathComparison.AreSame(confirmed, configuredPersistImagePath));

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
    /// Whether a changed capacity can be written straight into the header of the disk's existing
    /// image instead of marking the disk dirty (and so copying the whole image on the next save).
    /// The image qualifies only if the options keep the same image path and that file is the one the
    /// disk was last saved to or loaded from. A disk that is read-only or opted out of saving on
    /// exit is not patched: it must not touch its image at edit time, so it keeps the dirty mark
    /// and lets the normal save rules decide whether the image is ever written.
    /// </summary>
    /// <param name="currentPersistImagePath">The image path the disk uses now.</param>
    /// <param name="newPersistImagePath">The image path in the options being applied.</param>
    /// <param name="lastSavedImagePath">The image the disk was last saved to or loaded from.</param>
    /// <param name="saveImageOnExit">Whether the options being applied save the image on exit.</param>
    /// <param name="readOnly">Whether the options being applied make the disk read-only.</param>
    /// <returns><see langword="true"/> when the existing image may be patched.</returns>
    internal static bool CanPatchImageCapacity(
        string? currentPersistImagePath,
        string? newPersistImagePath,
        string? lastSavedImagePath,
        bool saveImageOnExit,
        bool readOnly) =>
        currentPersistImagePath is not null &&
        newPersistImagePath == currentPersistImagePath &&
        lastSavedImagePath == currentPersistImagePath &&
        saveImageOnExit &&
        !readOnly;

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
