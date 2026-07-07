namespace ManagedDrive.App.Models;

/// <summary>
/// Serializable snapshot of the settings required to recreate a RAM disk on startup.
/// Stored in the JSON settings file managed by <see cref="Services.SettingsStore"/>.
/// </summary>
public sealed record DiskProfile
{
    /// <summary>
    /// Gets or sets a value indicating whether this disk is re-mounted automatically on startup.
    /// </summary>
    public bool AutoMount
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the configured disk capacity in bytes.
    /// </summary>
    public ulong CapacityBytes
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the volume mount point (e.g., <c>"Z:"</c>).
    /// </summary>
    public string MountPoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional path to a disk image file used for persistence.
    /// </summary>
    public string? PersistImagePath
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the disk is mounted read-only.
    /// </summary>
    public bool ReadOnly
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the NTFS volume label.
    /// </summary>
    public string VolumeLabel { get; init; } = "RAM Disk";

    /// <summary>
    /// Gets or sets the optional auto-save interval in minutes. <c>null</c> disables auto-save.
    /// </summary>
    public uint? AutoSaveIntervalMinutes
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the compression level applied when the saved image is written.
    /// </summary>
    public ImageCompressionLevel CompressionLevel { get; init; } = ImageCompressionLevel.Fastest;

    /// <summary>
    /// Gets or sets the optional maximum number of retained snapshot images. <c>null</c>
    /// disables count-based snapshot pruning.
    /// </summary>
    public uint? MaxSnapshotCount
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the optional maximum total size, in bytes, of retained snapshot images.
    /// <c>null</c> disables size-based snapshot pruning.
    /// </summary>
    public ulong? MaxSnapshotSizeBytes
    {
        get; init;
    }
}