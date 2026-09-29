using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="GlobalMountCoordinator"/>.
/// </summary>
public sealed class GlobalMountCoordinatorTests
{
    /// <summary>
    /// Only drive letters get a global DOS-device symlink; a directory mount point is already
    /// reachable from other sessions and would be rejected by the helper.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <param name="expected">Whether it is published.</param>
    [Theory]
    [InlineData("R:", true)]
    [InlineData("z:", true)]
    [InlineData(@"C:\mnt\ramdisk", false)]
    [InlineData(@"D:\RamDisk\", false)]
    public void IsPublishable_MountPoint_PublishesOnlyDriveLetters(string mountPoint, bool expected)
    {
        var publishable = GlobalMountCoordinator.IsPublishable(mountPoint);

        Assert.Equal(expected, publishable);
    }
}
