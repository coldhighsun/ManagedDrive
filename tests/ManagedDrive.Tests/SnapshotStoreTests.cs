namespace ManagedDrive.Tests;

public sealed class SnapshotStoreTests
{
    [Theory]
    [InlineData(@"C:\disks\disk.mdr", @"C:\disks\disk.snapblobs")]
    [InlineData(@"C:\disks\my.image.mdr", @"C:\disks\my.image.snapblobs")]
    [InlineData(@"C:\disks\noext", @"C:\disks\noext.snapblobs")]
    public void ComputeBlobDirectory_AppendsSnapblobsSuffixNextToImage(string mainImagePath, string expected)
    {
        var result = SnapshotStore.ComputeBlobDirectory(mainImagePath);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ComputeBlobDirectory_NoDirectoryComponent_ResolvesRelativeToCurrentDirectory()
    {
        var result = SnapshotStore.ComputeBlobDirectory("disk.mdr");

        Assert.Equal("disk.snapblobs", result);
    }

    [Fact]
    public void HashToBlobPath_ShardsIntoTwoCharacterSubfolderFromHashHexPrefix()
    {
        var hash = Convert.FromHexString("ab34ef0000000000000000000000000000000000000000000000000000000000");
        var directory = @"C:\disks\disk.snapblobs";

        var result = SnapshotStore.HashToBlobPath(directory, hash);

        Assert.Equal(
            Path.Combine(directory, "ab", "ab34ef0000000000000000000000000000000000000000000000000000000000.blob"),
            result);
    }

    [Fact]
    public void HashToBlobPath_IsLowercaseRegardlessOfCasingConventions()
    {
        var hash = new byte[] { 0xAB, 0xCD, 0xEF };
        var directory = @"C:\disks\disk.snapblobs";

        var result = SnapshotStore.HashToBlobPath(directory, hash);
        var fileName = Path.GetFileName(result);

        Assert.DoesNotContain(fileName, char.IsUpper);
    }

    [Fact]
    public void HashToBlobPath_DifferentHashes_ProduceDifferentPaths()
    {
        var directory = @"C:\disks\disk.snapblobs";
        var first = SnapshotStore.HashToBlobPath(directory, [0x01, 0x02, 0x03]);
        var second = SnapshotStore.HashToBlobPath(directory, [0x04, 0x05, 0x06]);

        Assert.NotEqual(first, second);
    }
}
