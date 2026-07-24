namespace ManagedDrive.Core.Mounting;

/// <summary>
/// Per-field override values for a mount, sourced from CLI flags. Any field left <c>null</c>
/// defers to the saved profile (if any) and then to the built-in <see cref="DiskOptions"/>
/// default for that field. WPF/CLI-free counterpart of the app's <c>CliMountOverrides</c>, so
/// the merge logic can live in Core and be unit-tested.
/// </summary>
public sealed record MountOverrides
{
    /// <summary>Overrides the read-only flag when non-null.</summary>
    public bool? ReadOnly { get; init; }

    /// <summary>Overrides the auto-mount flag when non-null.</summary>
    public bool? AutoMount { get; init; }

    /// <summary>Overrides the auto-save interval when non-null.</summary>
    public uint? AutoSaveIntervalMinutes { get; init; }

    /// <summary>Overrides the compression level when non-null.</summary>
    public ImageCompressionLevel? CompressionLevel { get; init; }

    /// <summary>Overrides the maximum snapshot count when non-null.</summary>
    public uint? MaxSnapshotCount { get; init; }

    /// <summary>Overrides the maximum snapshot size when non-null.</summary>
    public ulong? MaxSnapshotSizeBytes { get; init; }

    /// <summary>Overrides the high-usage warning percentage when non-null.</summary>
    public double? HighUsageWarnPercent { get; init; }
}

/// <summary>
/// Builds the <see cref="DiskOptions"/> for a headless (CLI-driven) mount by merging, in
/// precedence order: the header/archive-derived mount point + capacity + label (always win), then
/// any explicit CLI <see cref="MountOverrides"/>, then a matching saved profile, then the built-in
/// <see cref="DiskOptions"/> defaults. Extracted so the previously duplicated merge in the image
/// and archive mount paths lives in one testable place.
/// </summary>
public static class MountOptionsFactory
{
    /// <summary>
    /// Builds options for mounting an existing <c>.mdr</c> image.
    /// </summary>
    /// <param name="savedProfile">
    /// The saved profile for this image path (already mapped to <see cref="DiskOptions"/>), or
    /// <c>null</c> when none exists.
    /// </param>
    /// <param name="mountPoint">The mount point to use.</param>
    /// <param name="imagePath">The image file path (used when no saved profile exists).</param>
    /// <param name="capacityBytes">The capacity read from the image header.</param>
    /// <param name="volumeLabel">The volume label read from the image header.</param>
    /// <param name="overrides">The explicit CLI overrides.</param>
    /// <returns>
    /// The merged options ready to mount.
    /// </returns>
    public static DiskOptions BuildImageOptions(
        DiskOptions? savedProfile,
        string mountPoint,
        string imagePath,
        ulong capacityBytes,
        string volumeLabel,
        MountOverrides overrides)
    {
        var baseOptions = savedProfile != null
            ? savedProfile with
            {
                MountPoint = mountPoint,
                CapacityBytes = capacityBytes,
                VolumeLabel = volumeLabel,
            }
            : new DiskOptions
            {
                MountPoint = mountPoint,
                CapacityBytes = capacityBytes,
                VolumeLabel = volumeLabel,
                PersistImagePath = imagePath,
            };

        return baseOptions with
        {
            ReadOnly = overrides.ReadOnly ?? baseOptions.ReadOnly,
            AutoMount = overrides.AutoMount ?? baseOptions.AutoMount,
            AutoSaveIntervalMinutes = overrides.AutoSaveIntervalMinutes ?? baseOptions.AutoSaveIntervalMinutes,
            CompressionLevel = overrides.CompressionLevel ?? baseOptions.CompressionLevel,
            MaxSnapshotCount = overrides.MaxSnapshotCount ?? baseOptions.MaxSnapshotCount,
            MaxSnapshotSizeBytes = overrides.MaxSnapshotSizeBytes ?? baseOptions.MaxSnapshotSizeBytes,
            HighUsageWarnPercent = overrides.HighUsageWarnPercent ?? baseOptions.HighUsageWarnPercent,
        };
    }

    /// <summary>
    /// Builds options for mounting an archive's contents. The disk is always read-only (no
    /// supported archive format allows writing changes back), so only the auto-mount flag is
    /// overridable.
    /// </summary>
    /// <param name="savedProfile">
    /// The saved profile for this archive path (already mapped to <see cref="DiskOptions"/>), or
    /// <c>null</c> when none exists.
    /// </param>
    /// <param name="mountPoint">The mount point to use.</param>
    /// <param name="archivePath">The archive file path.</param>
    /// <param name="capacityBytes">The uncompressed size derived from the archive.</param>
    /// <param name="volumeLabel">The volume label derived from the archive.</param>
    /// <param name="autoMountOverride">Overrides the auto-mount flag when non-null.</param>
    /// <returns>
    /// The merged, read-only options ready to mount.
    /// </returns>
    public static DiskOptions BuildArchiveOptions(
        DiskOptions? savedProfile,
        string mountPoint,
        string archivePath,
        ulong capacityBytes,
        string volumeLabel,
        bool? autoMountOverride)
    {
        var baseOptions = savedProfile != null
            ? savedProfile with
            {
                MountPoint = mountPoint,
                CapacityBytes = capacityBytes,
                VolumeLabel = volumeLabel,
            }
            : new DiskOptions
            {
                MountPoint = mountPoint,
                CapacityBytes = capacityBytes,
                VolumeLabel = volumeLabel,
                SourceArchivePath = archivePath,
            };

        return baseOptions with
        {
            ReadOnly = true,
            SourceArchivePath = archivePath,
            AutoMount = autoMountOverride ?? baseOptions.AutoMount,
        };
    }
}
