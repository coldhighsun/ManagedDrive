using ManagedDrive.App.Models;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

public sealed class DiskProfileMappingTests
{
    [Fact]
    public void ToProfile_ThenProfileToOptions_RoundTripsEveryField()
    {
        var options = new DiskOptions
        {
            MountPoint = "R:",
            VolumeLabel = "Test Label",
            CapacityBytes = 123_456_789UL,
            ReadOnly = true,
            AutoMount = true,
            PersistImagePath = @"C:\images\disk.mdr",
            SourceArchivePath = null,
            AutoSaveIntervalMinutes = 15,
            CompressionLevel = ImageCompressionLevel.SmallestSize,
            CustomZstdLevel = 19,
            MaxSnapshotCount = 7,
            MaxSnapshotSizeBytes = 999_000_000UL,
            HighUsageWarnPercent = 85.5,
            SaveImageOnExit = false,
        };

        var profile = MainViewModel.ToProfile(options);
        var roundTripped = MainViewModel.ProfileToOptions(profile);

        Assert.Equal(options, roundTripped);
    }

    [Fact]
    public void ToProfile_ThenProfileToOptions_RoundTripsFoldersAndEnvRedirects()
    {
        var options = new DiskOptions
        {
            MountPoint = "T:",
            CapacityBytes = 1_048_576UL,
            Folders = ["npm-cache", @"a\b"],
            EnvRedirects = [new() { Variable = "npm_config_cache", SubPath = "npm-cache" }],
        };

        var profile = MainViewModel.ToProfile(options);
        var roundTripped = MainViewModel.ProfileToOptions(profile);

        Assert.Equal(options.Folders, profile.Folders);
        Assert.Equal(options.EnvRedirects, profile.EnvRedirects);
        Assert.Equal(options, roundTripped);
    }

    [Fact]
    public void ToProfile_ThenProfileToOptions_RoundTripsNullableFieldsWhenUnset()
    {
        var options = new DiskOptions
        {
            MountPoint = "S:",
            VolumeLabel = "Minimal",
            CapacityBytes = 1_048_576UL,
        };

        var profile = MainViewModel.ToProfile(options);
        var roundTripped = MainViewModel.ProfileToOptions(profile);

        Assert.Equal(options, roundTripped);
    }

    [Fact]
    public void ProfileToOptions_ProfileWithBothArchiveAndImagePath_DropsImagePath()
    {
        var profile = new DiskProfile
        {
            MountPoint = "R:",
            SourceArchivePath = @"C:\archives\disk.zip",
            PersistImagePath = @"C:\images\disk.mdr",
        };

        var options = MainViewModel.ProfileToOptions(profile);

        Assert.Equal(@"C:\archives\disk.zip", options.SourceArchivePath);
        Assert.Null(options.PersistImagePath);
    }
}
