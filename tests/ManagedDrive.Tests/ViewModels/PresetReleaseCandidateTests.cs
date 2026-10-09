using ManagedDrive.App.Models;
using ManagedDrive.App.ViewModels;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests which disks and profiles may lose exclusive presets when another disk claims them.
/// </summary>
public sealed class PresetReleaseCandidateTests
{
    /// <summary>
    /// A writable disk on another mount point may lose presets.
    /// </summary>
    [Fact]
    public void CanLosePresets_WritableDiskElsewhere_ReturnsTrue()
    {
        var other = new DiskOptions { MountPoint = "R:", CapacityBytes = 1024 };

        Assert.True(MainViewModel.CanLosePresets(other, "T:"));
    }

    /// <summary>
    /// A read-only disk never applied its presets, so it owns nothing to take away.
    /// </summary>
    [Fact]
    public void CanLosePresets_ReadOnlyDisk_ReturnsFalse()
    {
        var other = new DiskOptions { MountPoint = "R:", CapacityBytes = 1024, ReadOnly = true };

        Assert.False(MainViewModel.CanLosePresets(other, "T:"));
    }

    /// <summary>
    /// The disk being created or edited is not one of the others, whatever the letter casing.
    /// </summary>
    [Fact]
    public void CanLosePresets_SameMountPoint_ReturnsFalse()
    {
        var other = new DiskOptions { MountPoint = "r:", CapacityBytes = 1024 };

        Assert.False(MainViewModel.CanLosePresets(other, "R:"));
    }

    /// <summary>
    /// Only writable saved profiles on another mount point may lose presets.
    /// </summary>
    /// <param name="readOnly">Whether the profile is read-only.</param>
    /// <param name="mountPoint">The profile's mount point.</param>
    /// <param name="expected">Whether it may lose presets.</param>
    [Theory]
    [InlineData(false, "R:", true)]
    [InlineData(true, "R:", false)]
    [InlineData(false, "t:", false)]
    public void CanLosePresets_Profile_SkipsReadOnlyAndSameMountPoint(bool readOnly, string mountPoint, bool expected)
    {
        var profile = new DiskProfile { MountPoint = mountPoint, CapacityBytes = 1024, ReadOnly = readOnly };

        Assert.Equal(expected, MainViewModel.CanLosePresets(profile, "T:"));
    }
}
