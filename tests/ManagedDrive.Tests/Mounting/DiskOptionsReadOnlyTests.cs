namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="DiskOptions.WithoutReadOnlyExtras"/>.
/// </summary>
public sealed class DiskOptionsReadOnlyTests
{
    /// <summary>
    /// Builds options carrying everything a read-only disk cannot use.
    /// </summary>
    /// <param name="readOnly">Whether the disk is read-only.</param>
    /// <returns>The options.</returns>
    private static DiskOptions Create(bool readOnly) => new()
    {
        MountPoint = "R:",
        CapacityBytes = 1024,
        ReadOnly = readOnly,
        HighUsageWarnPercent = 80,
        Folders = ["cache"],
        EnvRedirects = [new() { Variable = "TEMP", SubPath = "temp" }],
    };

    /// <summary>
    /// A read-only disk loses its preset folders, redirections and high-usage warning.
    /// </summary>
    [Fact]
    public void WithoutReadOnlyExtras_ReadOnly_DropsFoldersRedirectsAndWarning()
    {
        var options = Create(readOnly: true).WithoutReadOnlyExtras();

        Assert.Null(options.Folders);
        Assert.Null(options.EnvRedirects);
        Assert.Null(options.HighUsageWarnPercent);
    }

    /// <summary>
    /// A writable disk is returned unchanged.
    /// </summary>
    [Fact]
    public void WithoutReadOnlyExtras_Writable_ReturnsTheSameOptions()
    {
        var options = Create(readOnly: false);

        Assert.Same(options, options.WithoutReadOnlyExtras());
    }

    /// <summary>
    /// Presets apply only to a writable disk.
    /// </summary>
    /// <param name="readOnly">Whether the disk is read-only.</param>
    /// <param name="expected">Whether presets are applied.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AppliesPresets_ReadOnlyFlag_OnlyWritableDisksApplyThem(bool readOnly, bool expected)
    {
        Assert.Equal(expected, Create(readOnly).AppliesPresets());
    }

    /// <summary>
    /// A writable disk uses its stored threshold; a read-only disk never warns, even if one is stored.
    /// </summary>
    /// <param name="readOnly">Whether the disk is read-only.</param>
    /// <param name="expected">The expected effective threshold.</param>
    [Theory]
    [InlineData(false, 80.0)]
    [InlineData(true, null)]
    public void GetEffectiveHighUsageWarnPercent_ReadOnlyFlag_IgnoresStoredThresholdWhenReadOnly(bool readOnly, double? expected)
    {
        Assert.Equal(expected, Create(readOnly).GetEffectiveHighUsageWarnPercent());
    }
}
