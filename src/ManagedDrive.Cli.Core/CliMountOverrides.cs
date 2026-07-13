namespace ManagedDrive.Cli.Core;

/// <summary>
/// Overrides for mount options that can be specified via the CLI. These overrides take precedence over the default mount options and any previously saved mount options for a disk.
/// </summary>
public sealed record CliMountOverrides
{
    /// <summary>
    /// If <c>true</c>, the disk should be mounted read-only. If <c>false</c>, it should be mounted
    /// </summary>
    public bool? ReadOnly
    {
        get; init;
    }

    /// <summary>
    /// If <c>true</c>, the disk should be automatically mounted on application start. If <c>false</c>, it should not be automatically mounted.
    /// </summary>
    public bool? AutoMount
    {
        get; init;
    }

    /// <summary>
    /// Gets the auto-save interval in minutes.
    /// </summary>
    public uint? AutoSaveIntervalMinutes
    {
        get; init;
    }

    /// <summary>
    /// Gets the image compression level to apply.
    /// </summary>
    public ImageCompressionLevel? CompressionLevel
    {
        get; init;
    }

    /// <summary>
    /// Gets the maximum number of snapshots to retain.
    /// </summary>
    public uint? MaxSnapshotCount
    {
        get; init;
    }

    /// <summary>
    /// Gets the maximum snapshot size in bytes.
    /// </summary>
    public ulong? MaxSnapshotSizeBytes
    {
        get; init;
    }

    /// <summary>
    /// Gets the usage percentage threshold for issuing a high-usage warning.
    /// </summary>
    public double? HighUsageWarnPercent
    {
        get; init;
    }
}