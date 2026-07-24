using ManagedDrive.Core;

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
