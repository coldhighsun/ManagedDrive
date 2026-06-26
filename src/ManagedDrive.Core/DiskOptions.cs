namespace ManagedDrive.Core;

/// <summary>
/// Immutable configuration record used to create and mount a RAM disk.
/// </summary>
public sealed record DiskOptions
{
    /// <summary>
    /// Total capacity of the RAM disk in bytes.
    /// </summary>
    public required ulong CapacityBytes
    {
        get; init;
    }

    /// <summary>
    /// The mount point for the volume. Use a drive letter (e.g., <c>"Z:"</c>) or an empty
    /// NTFS directory path.
    /// </summary>
    public required string MountPoint
    {
        get; init;
    }

    /// <summary>
    /// NTFS volume label displayed in File Explorer.
    /// </summary>
    public string VolumeLabel { get; init; } = "RAM Disk";

    /// <summary>
    /// When <c>true</c> the mounted volume is read-only.
    /// </summary>
    public bool ReadOnly
    {
        get; init;
    }

    /// <summary>
    /// Optional path to a disk image file used for persistence.
    /// When the file exists it is loaded on mount; call <see cref="RamDisk.SaveToImage"/> to
    /// write it back.
    /// </summary>
    public string? PersistImagePath
    {
        get; init;
    }

    /// <summary>
    /// When <c>true</c> this disk is re-mounted automatically on next application startup.
    /// </summary>
    public bool AutoMount
    {
        get; init;
    }
}