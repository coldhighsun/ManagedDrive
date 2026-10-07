namespace ManagedDrive.Core.DiskCreation;

/// <summary>
/// A template for a disk: suggested size and label, the folders to create on every mount and the
/// environment variables to point into the disk. Several presets can be combined on one disk with
/// <see cref="PresetComposer"/>.
/// </summary>
public sealed record DiskPreset
{
    /// <summary>
    /// Gets the stable identifier. Built-in presets use a fixed lower-case id; user presets get a
    /// generated one.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the display name of a user preset. <c>null</c> for built-in presets, whose names are
    /// localized by the app from the id.
    /// </summary>
    public string? Name
    {
        get; init;
    }

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

    /// <summary>
    /// Gets a value indicating whether the disk is also offered as the user's temp directory.
    /// </summary>
    public bool SetAsTemp
    {
        get; init;
    }
}
