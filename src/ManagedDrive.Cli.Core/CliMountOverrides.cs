using ManagedDrive.Core;

namespace ManagedDrive.Cli.Core;

/// <summary>
/// Optional per-field overrides for the <c>mount</c> CLI command. Every field is nullable: a
/// <c>null</c> value means the user did not pass the corresponding flag, so the host application
/// should keep whatever value it would otherwise use (a matching saved disk profile if one exists
/// for the image path, or the <see cref="DiskOptions"/> default). A non-null value means the user
/// explicitly passed the flag, and it must win over any saved profile or default.
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