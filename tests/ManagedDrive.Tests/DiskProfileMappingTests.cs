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
            SourceArchivePath = @"C:\archives\disk.zip",
            AutoSaveIntervalMinutes = 15,
            CompressionLevel = ImageCompressionLevel.SmallestSize,
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
}
