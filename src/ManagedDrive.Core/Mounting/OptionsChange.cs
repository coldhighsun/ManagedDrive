namespace ManagedDrive.Core.Mounting;

/// <summary>
/// What <see cref="RamDisk.TryApplyOptions"/> did: the options its edit was given and the options
/// the edit computed from them and that were applied. They are equal when the edit found nothing
/// to change.
/// </summary>
/// <param name="Before">The disk's options the edit ran on.</param>
/// <param name="After">
/// The options the edit computed and that were applied. A volume label renamed in Explorer while
/// the apply waited is kept on the disk, so the disk's own label can differ from this one's.
/// </param>
public sealed record OptionsChange(DiskOptions Before, DiskOptions After)
{
    /// <summary>
    /// Gets a value indicating whether the edit changed anything.
    /// </summary>
    public bool Changed => Before != After;
}
