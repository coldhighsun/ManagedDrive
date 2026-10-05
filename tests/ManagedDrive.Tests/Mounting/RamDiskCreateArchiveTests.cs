namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="RamDisk.Create"/> option validation that fails before any WinFsp host exists.
/// </summary>
public sealed class RamDiskCreateArchiveTests
{
    /// <summary>
    /// A source archive that no longer exists fails the mount instead of mounting an empty writable disk.
    /// </summary>
    [Fact]
    public void Create_SourceArchiveMissing_ThrowsFileNotFoundException()
    {
        var options = new DiskOptions
        {
            MountPoint = "R:",
            CapacityBytes = 1024 * 1024,
            SourceArchivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip"),
        };

        Assert.Throws<FileNotFoundException>(() => RamDisk.Create(options, cancellationToken: TestContext.Current.CancellationToken));
    }
}
