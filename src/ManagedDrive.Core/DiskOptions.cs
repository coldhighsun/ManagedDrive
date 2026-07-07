namespace ManagedDrive.Core;

/// <summary>
/// Compression level applied when saving a <c>.mdr</c> disk image.
/// </summary>
public enum ImageCompressionLevel
{
    /// <summary>
    /// No compression; the image is written uncompressed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Lowest compression ratio, optimized for write speed.
    /// </summary>
    Fastest = 1,

    /// <summary>
    /// Balanced compression ratio and speed. Default.
    /// </summary>
    Optimal = 2,

    /// <summary>
    /// Highest compression ratio, at the cost of write speed.
    /// </summary>
    SmallestSize = 3,
}

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

    /// <summary>
    /// Optional interval, in minutes, at which the disk contents are automatically saved to
    /// <see cref="PersistImagePath"/>. <c>null</c> disables auto-save.
    /// </summary>
    public uint? AutoSaveIntervalMinutes
    {
        get; init;
    }

    /// <summary>
    /// Compression level applied when the saved <c>.mdr</c> image is written.
    /// </summary>
    public ImageCompressionLevel CompressionLevel { get; init; } = ImageCompressionLevel.Fastest;

    /// <summary>
    /// Optional maximum number of timestamped snapshot images to retain alongside
    /// <see cref="PersistImagePath"/>. <c>null</c> disables count-based snapshot pruning.
    /// Snapshots are only written when auto-save is enabled.
    /// </summary>
    public uint? MaxSnapshotCount
    {
        get; init;
    }

    /// <summary>
    /// Optional maximum total size, in bytes, of all retained snapshot images.
    /// <c>null</c> disables size-based snapshot pruning. Snapshots are only written when
    /// auto-save is enabled.
    /// </summary>
    public ulong? MaxSnapshotSizeBytes
    {
        get; init;
    }
}