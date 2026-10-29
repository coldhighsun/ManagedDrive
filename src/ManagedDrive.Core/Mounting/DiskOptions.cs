namespace ManagedDrive.Core.Mounting;

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
    /// Optional path to an archive file (zip, 7z, rar, tar, or any other format
    /// <c>SharpCompress</c> can read) whose contents are extracted into this disk on every mount
    /// via <see cref="ArchiveNodeMapBuilder"/>. Mutually exclusive with
    /// <see cref="PersistImagePath"/> — a disk has at most one content source. Disks with a
    /// non-null <see cref="SourceArchivePath"/> are always mounted read-only, since none of the
    /// supported archive formats support writing changes back.
    /// </summary>
    public string? SourceArchivePath
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
    /// Zstd compression level applied when the saved <c>.mdr</c> image is written: 0 disables
    /// compression, 1 (fastest) to 22 (smallest) are Zstd levels. See <see cref="ZstdLevels"/>.
    /// </summary>
    public int CompressionLevel { get; init; } = ZstdLevels.Default;

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

    /// <summary>
    /// Optional usage percentage (0-100) at which this disk is flagged as high-usage and a tray
    /// warning is shown. <c>null</c> disables the warning for this disk. Defaults to 90%.
    /// </summary>
    public double? HighUsageWarnPercent { get; init; } = 90.0;

    /// <summary>
    /// When <c>true</c> (the default), the disk image is saved on application exit and OS
    /// shutdown. Has no effect on read-only disks or when <see cref="PersistImagePath"/> is
    /// <c>null</c>. Independent of periodic auto-save (<see cref="AutoSaveIntervalMinutes"/>).
    /// </summary>
    public bool SaveImageOnExit { get; init; } = true;

    /// <summary>
    /// Folders, relative to the disk's root, created after every mount. A disk without an image
    /// starts empty each time, so these are recreated rather than stored in the disk. <c>null</c>
    /// or empty creates none.
    /// </summary>
    public IReadOnlyList<string>? Folders
    {
        get; init;
    }

    /// <summary>
    /// Per-user environment variables pointed into this disk while it is mounted and restored when
    /// it is unmounted. <c>null</c> or empty redirects none.
    /// </summary>
    public IReadOnlyList<EnvRedirect>? EnvRedirects
    {
        get; init;
    }

    /// <summary>
    /// Returns whether the disk's folders and environment variable redirections are applied. A
    /// read-only disk cannot hold the folders, and a variable pointing at it would break writes, so
    /// its presets (kept in the options by a one-time CLI override or a read-only profile) are
    /// ignored. A method rather than a property so it stays out of the record's printed members and
    /// of any property-based serializer; <c>DiskProfile.AppliesPresets()</c> is its profile twin.
    /// </summary>
    /// <returns><c>true</c> if the presets are applied.</returns>
    public bool AppliesPresets() => !ReadOnly;

    /// <summary>
    /// Returns the folders, relative to the disk's root, that applying the presets creates: the
    /// plain <see cref="Folders"/> and the folder of every <see cref="EnvRedirects"/> entry. Empty
    /// when the presets are not applied (see <see cref="AppliesPresets"/>).
    /// </summary>
    /// <returns>The relative folder paths, in the order they are created.</returns>
    public IReadOnlyList<string> GetPresetFolderPaths() =>
        AppliesPresets()
            ? [.. Folders ?? [], .. (EnvRedirects ?? []).Select(r => r.SubPath)]
            : [];

    /// <summary>
    /// Returns the high-usage warning threshold that is in effect: <see cref="HighUsageWarnPercent"/>,
    /// except that a read-only disk never warns since its usage never grows.
    /// </summary>
    /// <returns>The threshold percentage, or <c>null</c> when no warning applies.</returns>
    public double? GetEffectiveHighUsageWarnPercent() => ReadOnly ? null : HighUsageWarnPercent;

    /// <summary>
    /// Returns these options without what a read-only disk cannot use (see
    /// <see cref="AppliesPresets"/> and <see cref="GetEffectiveHighUsageWarnPercent"/>): the folders
    /// and redirections of presets and the high-usage warning. Options of a writable disk are
    /// returned unchanged. Applied where a disk is made read-only on purpose (the create/edit
    /// dialog, archive mounts). Deliberately not applied when options are read back from a saved
    /// profile or when the CLI image mount overrides <see cref="ReadOnly"/> once: the profile keeps
    /// its presets and warning, and the consumers that matter go through the two methods above.
    /// </summary>
    /// <returns>The options with the read-only exclusions applied.</returns>
    public DiskOptions WithoutReadOnlyExtras() =>
        ReadOnly ? this with { Folders = null, EnvRedirects = null, HighUsageWarnPercent = null } : this;
}
