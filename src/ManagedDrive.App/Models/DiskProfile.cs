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
    /// Gets or sets the optional path to an archive file whose contents are extracted into this
    /// disk on every mount. Mutually exclusive with <see cref="PersistImagePath"/>.
    /// </summary>
    public string? SourceArchivePath
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
    /// Gets or sets the optional advanced override (1-22) of the exact Zstd level used instead of
    /// the preset mapped from <see cref="CompressionLevel"/>. <c>null</c> means use the preset.
    /// </summary>
    public int? CustomZstdLevel
    {
        get; init;
    }

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

    /// <summary>
    /// Gets or sets the optional usage percentage (0-100) at which this disk is flagged as
    /// high-usage. <c>null</c> disables the warning for this disk.
    /// </summary>
    public double? HighUsageWarnPercent { get; init; } = 90.0;

    /// <summary>
    /// Gets or sets a value indicating whether the disk image is saved on application exit and
    /// OS shutdown. Defaults to <c>true</c>.
    /// </summary>
    public bool SaveImageOnExit { get; init; } = true;

    /// <summary>
    /// Gets or sets the ids of the presets that redirect environment variables into the disk. Only
    /// the ids are saved for these: their folders and variables are known from the preset, and
    /// whether one is in effect is read from the environment. <c>null</c> for none.
    /// </summary>
    public IReadOnlyList<string>? PresetIds
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the folders, relative to the disk's root, created after every mount, besides
    /// those of <see cref="PresetIds"/>.
    /// </summary>
    public IReadOnlyList<string>? Folders
    {
        get; init;
    }

    /// <summary>
    /// Gets or sets the environment variables pointed into the disk while it is mounted, besides
    /// those of <see cref="PresetIds"/>.
    /// </summary>
    public IReadOnlyList<EnvRedirect>? EnvRedirects
    {
        get; init;
    }

    /// <summary>
    /// Returns whether the disk's folders and environment variable redirections are applied when it
    /// is mounted. A method (not a property) so it is not written to the settings file. Mirrors
    /// <see cref="Core.Mounting.DiskOptions.AppliesPresets"/>: a read-only disk applies no presets.
    /// </summary>
    /// <returns><c>true</c> if the profile's presets are applied.</returns>
    public bool AppliesPresets() => !ReadOnly;
}
