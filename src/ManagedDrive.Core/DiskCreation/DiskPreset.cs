namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// A template for a disk: suggested size and label, the folders to create on every mount and the
/// environment variables to point into the disk. Several presets can be combined on one disk with
/// <see cref="PresetComposer"/>.
/// </summary>
public sealed record DiskPreset
{
    /// <summary>
    /// Gets the stable, fixed lower-case identifier; the app localizes the preset's name from it.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the suggested capacity in bytes.
    /// </summary>
    public ulong CapacityBytes
    {
        get; init;
    }

    /// <summary>
    /// Gets the suggested volume label, or <c>null</c> to keep the default.
    /// </summary>
    public string? VolumeLabel
    {
        get; init;
    }

    /// <summary>
    /// Gets the suggested compression level, or <c>null</c> to keep the default.
    /// </summary>
    public ImageCompressionLevel? CompressionLevel
    {
        get; init;
    }

    /// <summary>
    /// Gets the folders, relative to the disk's root, created on every mount.
    /// </summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>
    /// Gets the environment variables pointed into the disk.
    /// </summary>
    public IReadOnlyList<EnvRedirect> EnvRedirects { get; init; } = [];
}
