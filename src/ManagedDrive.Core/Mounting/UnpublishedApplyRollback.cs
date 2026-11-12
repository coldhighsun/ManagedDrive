namespace ManagedDrive.Core.Mounting;

/// <summary>
/// What an options apply that failed before publishing its options takes back. Pure, so the steps
/// are tested without a mounted disk.
/// </summary>
internal static class UnpublishedApplyRollback
{
    /// <summary>
    /// Takes back the volume label when it was already changed, and the capacity when phase 1
    /// changed it. A capacity taken back, or one that could not be (usage grew past it meanwhile, so
    /// the larger capacity stays published), marks the disk dirty, because the image header may
    /// already have been patched to the abandoned capacity.
    /// </summary>
    /// <param name="labelUpdated">Whether the file system's volume label was already changed.</param>
    /// <param name="capacityChanged">Whether phase 1 changed the capacity.</param>
    /// <param name="restoreLabel">Puts the old volume label back.</param>
    /// <param name="tryRestoreCapacity">
    /// Puts the old capacity back; returns the reason it could not, or <c>null</c> when it did.
    /// </param>
    /// <param name="capacityKept">Reports that the capacity could not be taken back, with the reason.</param>
    /// <param name="markDirty">Marks the disk as needing a save.</param>
    internal static void Undo(
        bool labelUpdated,
        bool capacityChanged,
        Action restoreLabel,
        Func<string?> tryRestoreCapacity,
        Action<string> capacityKept,
        Action markDirty)
    {
        ArgumentNullException.ThrowIfNull(restoreLabel);
        ArgumentNullException.ThrowIfNull(tryRestoreCapacity);
        ArgumentNullException.ThrowIfNull(capacityKept);
        ArgumentNullException.ThrowIfNull(markDirty);

        if (labelUpdated)
        {
            restoreLabel();
        }

        if (!capacityChanged)
        {
            return;
        }

        if (tryRestoreCapacity() is { } error)
        {
            capacityKept(error);
        }

        markDirty();
    }
}
