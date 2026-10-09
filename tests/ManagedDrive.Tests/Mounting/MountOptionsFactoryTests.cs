namespace ManagedDrive.Tests;

public sealed class MountOptionsFactoryTests
{
    private const string Image = @"C:\images\disk.mdr";
    private const string Archive = @"C:\data\archive.zip";

    [Fact]
    public void BuildImageOptions_NoProfileNoOverrides_UsesHeaderValuesAndDefaults()
    {
        var options = MountOptionsFactory.BuildImageOptions(
            savedProfile: null, mountPoint: "R:", imagePath: Image,
            capacityBytes: 8UL * 1024 * 1024, volumeLabel: "Vol", overrides: new());

        Assert.Equal("R:", options.MountPoint);
        Assert.Equal(8UL * 1024 * 1024, options.CapacityBytes);
        Assert.Equal("Vol", options.VolumeLabel);
        Assert.Equal(Image, options.PersistImagePath);
        // Defaults preserved.
        Assert.False(options.ReadOnly);
        Assert.Equal(ImageCompressionLevel.Fastest, options.CompressionLevel);
        Assert.True(options.SaveImageOnExit);
    }

    [Fact]
    public void BuildImageOptions_WithProfile_ReusesProfileFieldsButHeaderWins()
    {
        var profile = new DiskOptions
        {
            MountPoint = "OLD:",
            CapacityBytes = 1,
            VolumeLabel = "OldLabel",
            PersistImagePath = Image,
            ReadOnly = true,
            AutoMount = true,
            CompressionLevel = ImageCompressionLevel.SmallestSize,
            HighUsageWarnPercent = 75,
        };

        var options = MountOptionsFactory.BuildImageOptions(
            profile, mountPoint: "R:", imagePath: Image,
            capacityBytes: 8UL * 1024 * 1024, volumeLabel: "NewLabel", overrides: new());

        // Header-derived values always win.
        Assert.Equal("R:", options.MountPoint);
        Assert.Equal(8UL * 1024 * 1024, options.CapacityBytes);
        Assert.Equal("NewLabel", options.VolumeLabel);
        // Profile fields reused when no override.
        Assert.True(options.ReadOnly);
        Assert.True(options.AutoMount);
        Assert.Equal(ImageCompressionLevel.SmallestSize, options.CompressionLevel);
        Assert.Equal(75, options.HighUsageWarnPercent);
    }

    /// <summary>
    /// A one-time read-only override keeps the saved profile's presets and usage warning in the
    /// options, so saving the settings afterwards does not strip them from the profile.
    /// </summary>
    [Fact]
    public void BuildImageOptions_ReadOnlyOverrideOnWritableProfile_KeepsPresetsAndWarning()
    {
        var profile = new DiskOptions
        {
            MountPoint = "OLD:",
            CapacityBytes = 1,
            PersistImagePath = Image,
            HighUsageWarnPercent = 80,
            Folders = ["npm-cache"],
            EnvRedirects = [new() { Variable = "npm_config_cache", SubPath = "npm-cache" }],
        };

        var options = MountOptionsFactory.BuildImageOptions(
            profile, mountPoint: "R:", imagePath: Image,
            capacityBytes: 8UL * 1024 * 1024, volumeLabel: "Vol", overrides: new() { ReadOnly = true });

        Assert.True(options.ReadOnly);
        Assert.Equal(80, options.HighUsageWarnPercent);
        Assert.Equal(["npm-cache"], options.Folders);
        Assert.Single(options.EnvRedirects!);
    }

    /// <summary>
    /// An archive disk is always read-only, so a saved profile's presets and usage warning are dropped.
    /// </summary>
    [Fact]
    public void BuildArchiveOptions_ProfileWithPresets_DropsPresetsAndWarning()
    {
        var profile = new DiskOptions
        {
            MountPoint = "OLD:",
            CapacityBytes = 1,
            HighUsageWarnPercent = 80,
            Folders = ["npm-cache"],
            EnvRedirects = [new() { Variable = "npm_config_cache", SubPath = "npm-cache" }],
        };

        var options = MountOptionsFactory.BuildArchiveOptions(
            profile, mountPoint: "R:", archivePath: Archive,
            capacityBytes: 8UL * 1024 * 1024, volumeLabel: "Vol", autoMountOverride: null);

        Assert.Null(options.Folders);
        Assert.Null(options.EnvRedirects);
        Assert.Null(options.HighUsageWarnPercent);
    }

    [Fact]
    public void BuildImageOptions_ProfileWithSourceArchivePath_ClearsSourceArchivePath()
    {
        var profile = new DiskOptions
        {
            MountPoint = "OLD:",
            CapacityBytes = 1,
            PersistImagePath = Image,
            SourceArchivePath = Archive,
        };

        var options = MountOptionsFactory.BuildImageOptions(
            profile, mountPoint: "R:", imagePath: Image,
            capacityBytes: 8UL * 1024 * 1024, volumeLabel: "Vol", overrides: new());

        Assert.Null(options.SourceArchivePath);
        Assert.Equal(Image, options.PersistImagePath);
    }

    [Fact]
    public void BuildImageOptions_OverridesWinOverProfile()
    {
        var profile = new DiskOptions
        {
            MountPoint = "OLD:",
            CapacityBytes = 1,
            VolumeLabel = "L",
            PersistImagePath = Image,
            ReadOnly = false,
            CompressionLevel = ImageCompressionLevel.Fastest,
        };

        var overrides = new MountOverrides
        {
            ReadOnly = true,
            CompressionLevel = ImageCompressionLevel.Optimal,
            MaxSnapshotCount = 5,
        };

        var options = MountOptionsFactory.BuildImageOptions(
            profile, "R:", Image, 4UL * 1024 * 1024, "L", overrides);

        Assert.True(options.ReadOnly);
        Assert.Equal(ImageCompressionLevel.Optimal, options.CompressionLevel);
        Assert.Equal(5U, options.MaxSnapshotCount);
    }

    [Fact]
    public void BuildImageOptions_CustomZstdLevelOverrideWinsOverProfile()
    {
        var profile = new DiskOptions
        {
            MountPoint = "OLD:",
            CapacityBytes = 1,
            VolumeLabel = "L",
            PersistImagePath = Image,
            CompressionLevel = ImageCompressionLevel.Optimal,
            CustomZstdLevel = 5,
        };

        var overrides = new MountOverrides { CustomZstdLevel = 19 };

        var options = MountOptionsFactory.BuildImageOptions(
            profile, "R:", Image, 4UL * 1024 * 1024, "L", overrides);

        Assert.Equal(19, options.CustomZstdLevel);
    }

    [Fact]
    public void BuildImageOptions_NoCustomZstdLevelOverride_ReusesProfileValue()
    {
        var profile = new DiskOptions
        {
            MountPoint = "OLD:",
            CapacityBytes = 1,
            VolumeLabel = "L",
            PersistImagePath = Image,
            CompressionLevel = ImageCompressionLevel.Optimal,
            CustomZstdLevel = 5,
        };

        var options = MountOptionsFactory.BuildImageOptions(
            profile, "R:", Image, 4UL * 1024 * 1024, "L", overrides: new());

        Assert.Equal(5, options.CustomZstdLevel);
    }

    [Fact]
    public void BuildArchiveOptions_ForcesReadOnlyAndSetsSourcePath()
    {
        var options = MountOptionsFactory.BuildArchiveOptions(
            savedProfile: null, mountPoint: "R:", archivePath: Archive,
            capacityBytes: 16UL * 1024 * 1024, volumeLabel: "Arc", autoMountOverride: null);

        Assert.True(options.ReadOnly);
        Assert.Equal(Archive, options.SourceArchivePath);
        Assert.Null(options.PersistImagePath);
        Assert.Equal("R:", options.MountPoint);
        Assert.Equal("Arc", options.VolumeLabel);
        Assert.False(options.AutoMount);
    }

    [Fact]
    public void BuildArchiveOptions_AutoMountOverrideApplies()
    {
        var options = MountOptionsFactory.BuildArchiveOptions(
            savedProfile: null, mountPoint: "R:", archivePath: Archive,
            capacityBytes: 1, volumeLabel: "A", autoMountOverride: true);

        Assert.True(options.AutoMount);
    }

    [Fact]
    public void BuildArchiveOptions_StaysReadOnlyEvenWithWritableProfile()
    {
        var profile = new DiskOptions
        {
            MountPoint = "OLD:",
            CapacityBytes = 1,
            VolumeLabel = "L",
            SourceArchivePath = Archive,
            ReadOnly = false,
            AutoMount = true,
        };

        var options = MountOptionsFactory.BuildArchiveOptions(
            profile, "R:", Archive, 2UL * 1024 * 1024, "L", autoMountOverride: null);

        Assert.True(options.ReadOnly);
        Assert.True(options.AutoMount);
    }
}
