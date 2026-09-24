using ManagedDrive.App.Views;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the export-path validation in <see cref="CloneDiskDialog"/>.
/// </summary>
public sealed class CloneDiskDialogTests
{
    /// <summary>
    /// Exporting over another active disk's image is rejected; paths compare case-insensitively.
    /// </summary>
    [Fact]
    public void GetExportPathError_PathIsAnotherDisksImage_ReturnsInUseKey()
    {
        DiskOptions[] disks = [Disk("R:", @"C:\images\Data.mdr")];

        var error = CloneDiskDialog.GetExportPathError(@"c:\IMAGES\data.mdr", disks, ["R:"]);

        Assert.Equal("Val.ImagePathInUse", error);
    }

    /// <summary>
    /// Exporting to a name that looks like a snapshot index is rejected.
    /// </summary>
    [Fact]
    public void GetExportPathError_SnapshotFileName_ReturnsSnapshotKey()
    {
        var error = CloneDiskDialog.GetExportPathError(@"C:\images\disk.20260707-220900.mdr", [], []);

        Assert.Equal("Val.ImagePathIsSnapshot", error);
    }

    /// <summary>
    /// A path no active disk uses is accepted.
    /// </summary>
    [Fact]
    public void GetExportPathError_UnusedPath_ReturnsNull()
    {
        DiskOptions[] disks = [Disk("R:", @"C:\images\data.mdr"), Disk("S:", null)];

        var error = CloneDiskDialog.GetExportPathError(@"C:\images\data.zip", disks, ["R:", "S:"]);

        Assert.Null(error);
    }

    /// <summary>
    /// Exporting onto a path within any currently active disk (including the source disk being
    /// exported) is rejected, whether or not any disk's image happens to live there.
    /// </summary>
    [Fact]
    public void GetExportPathError_PathOnActiveDisk_ReturnsOnRamDiskKey()
    {
        var error = CloneDiskDialog.GetExportPathError(@"R:\backup.mdr", [], ["R:"]);

        Assert.Equal("Val.ImagePathOnRamDisk", error);
    }

    /// <summary>
    /// Creates the options of an active disk mounted at <paramref name="mountPoint"/>.
    /// </summary>
    /// <param name="mountPoint">The disk's drive letter.</param>
    /// <param name="imagePath">The disk's image file, or <c>null</c> for a non-persistent disk.</param>
    /// <returns>The disk options.</returns>
    private static DiskOptions Disk(string mountPoint, string? imagePath) => new()
    {
        CapacityBytes = 64UL * 1024 * 1024,
        MountPoint = mountPoint,
        PersistImagePath = imagePath,
    };
}
