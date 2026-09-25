using ManagedDrive.App.Models;
using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="TempDirCompatChecker"/>'s WPF-free helpers.
/// </summary>
public sealed class TempDirCompatCheckerTests
{
    /// <summary>
    /// A TEMP path is matched to the profile whose mount point contains it, whether that is a
    /// drive letter or a directory mount point; a mere name prefix doesn't count.
    /// </summary>
    /// <param name="path">The TEMP path.</param>
    /// <param name="expectedMountPoint">The expected profile's mount point, or <c>null</c> for none.</param>
    [Theory]
    [InlineData(@"R:\Temp", "R:")]
    [InlineData(@"r:\", "R:")]
    [InlineData(@"C:\mnt\ramdisk\Temp", @"C:\mnt\ramdisk")]
    [InlineData(@"C:\mnt\ramdisk", @"C:\mnt\ramdisk")]
    [InlineData(@"C:\mnt\ramdisk2\Temp", null)]
    [InlineData(@"C:\Users\USER\AppData\Local\Temp", null)]
    [InlineData(@"S:\Temp", null)]
    public void FindProfileContainingPath_Path_ReturnsContainingProfile(string path, string? expectedMountPoint)
    {
        DiskProfile[] profiles =
        [
            new() { MountPoint = "R:" },
            new() { MountPoint = @"C:\mnt\ramdisk" },
        ];

        var profile = TempDirCompatChecker.FindProfileContainingPath(path, profiles);

        Assert.Equal(expectedMountPoint, profile?.MountPoint);
    }

    /// <summary>
    /// When mount points are nested, the most specific one wins.
    /// </summary>
    [Fact]
    public void FindProfileContainingPath_NestedMountPoints_ReturnsMostSpecificProfile()
    {
        DiskProfile[] profiles =
        [
            new() { MountPoint = @"C:\mnt\outer\inner\" },
            new() { MountPoint = @"C:\mnt\outer" },
        ];

        var profile = TempDirCompatChecker.FindProfileContainingPath(@"C:\mnt\outer\inner\Temp", profiles);

        Assert.Equal(@"C:\mnt\outer\inner\", profile?.MountPoint);
    }

    /// <summary>
    /// Specificity is judged on the normalized mount point, not the raw string: a longer but
    /// non-canonical spelling of the outer mount point doesn't beat the inner one.
    /// </summary>
    [Fact]
    public void FindProfileContainingPath_NonCanonicalOuterMountPoint_ReturnsInnerProfile()
    {
        DiskProfile[] profiles =
        [
            new() { MountPoint = @"C:\mnt\outer\inner\..\..\outer" },
            new() { MountPoint = @"C:\mnt\outer\inner" },
        ];

        var profile = TempDirCompatChecker.FindProfileContainingPath(@"C:\mnt\outer\inner\Temp", profiles);

        Assert.Equal(@"C:\mnt\outer\inner", profile?.MountPoint);
    }
}
