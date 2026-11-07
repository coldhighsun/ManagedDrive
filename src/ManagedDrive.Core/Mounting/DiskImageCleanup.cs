namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Thrown when a disk's image file or one of its snapshots could not be deleted (for example
/// because another program has it open).
/// </summary>
public sealed class DiskImageCleanupException(string imagePath, string message) : IOException(message)
{
    /// <summary>
    /// Gets the path of the image file whose cleanup failed.
    /// </summary>
    public string ImagePath { get; } = imagePath;
}

/// <summary>
/// Deletes a disk's image file(s) together with their snapshots, in an order that never leaves the
/// data with fewer copies than before a failed attempt.
/// </summary>
internal static class DiskImageCleanup
{
    private static readonly ILogger Logger = AppLog.CreateLogger(typeof(DiskImageCleanup));

    /// <summary>
    /// Deletes every snapshot of <paramref name="imagePath"/> (and its blob store) first, and the
    /// image file itself only once all of them are gone. See
    /// <see cref="DeleteImagesAndSnapshots"/>.
    /// </summary>
    /// <param name="imagePath">The disk's image file.</param>
    /// <exception cref="DiskImageCleanupException">
    /// Thrown when a snapshot or the image could not be deleted.
    /// </exception>
    internal static void DeleteImageAndSnapshots(string imagePath) => DeleteImagesAndSnapshots([imagePath]);

    /// <summary>
    /// Deletes the snapshots of every path in <paramref name="imagePaths"/> first, and the image
    /// files only once all snapshots are gone. Both phases go from the last path to the first. The image is the disk's only persistent copy of its
    /// contents, so images must be the last things to go: if any snapshot cannot be deleted this
    /// throws with every image still in place, however many paths there are.
    /// </summary>
    /// <param name="imagePaths">
    /// The image files to clean up (a disk can have two, see remarks), the disk's current image
    /// first. Everything is deleted in reverse order, so the current image and its snapshots go
    /// last.
    /// </param>
    /// <exception cref="DiskImageCleanupException">
    /// Thrown for the first snapshot set that could not be deleted (the remaining paths are not
    /// touched), or else for the first image that could not be deleted (the images after it, the
    /// disk's current one among them, are not touched).
    /// </exception>
    /// <remarks>
    /// A disk has two image paths when its configured path was changed after the last save: the
    /// old file still holds the disk's previous contents.
    /// </remarks>
    internal static void DeleteImagesAndSnapshots(IReadOnlyList<string> imagePaths)
    {
        // Last to first, like the images below: the first path is the one the disk uses now, so
        // its snapshot history is the last to be touched. Stops at the first path whose snapshots
        // cannot all be deleted: the caller aborts the format then, so the snapshots of the
        // remaining paths must not be destroyed for nothing.
        // DeleteAllSnapshots only logs individual failures and reports whether any was left over.
        for (var i = imagePaths.Count - 1; i >= 0; i--)
        {
            var imagePath = imagePaths[i];
            if (!SnapshotManager.DeleteAllSnapshots(imagePath))
            {
                throw new DiskImageCleanupException(
                    imagePath,
                    $"Could not delete all snapshots of '{imagePath}'; the images were left in place.");
            }
        }

        // Last to first: the first path is the one the disk uses now, the only copy of its current
        // data, so it goes last. Stops at the first image that cannot be deleted, so a stuck older
        // file aborts the format while the current image is still there.
        for (var i = imagePaths.Count - 1; i >= 0; i--)
        {
            var imagePath = imagePaths[i];
            try
            {
                File.Delete(imagePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Failed to delete image '{Path}'", imagePath);
                throw new DiskImageCleanupException(imagePath, $"Could not delete the image '{imagePath}'.");
            }
        }
    }
}
