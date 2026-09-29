namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemCapacityTests
{
    [Fact]
    public void TryUpdateCapacity_IncreasingCapacity_Succeeds()
    {
        var fs = new MemoryFileSystem(1024, "Label");

        var ok = fs.TryUpdateCapacity(2048);

        Assert.True(ok);
    }

    [Fact]
    public void TryUpdateCapacity_ReducingBelowCurrentUsage_ReturnsFalseAndLeavesCapacityUnchanged()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\big.bin", 0, 0, (uint)FileAttributes.Normal, [], 512 * 1024,
            out _, out _, out _, out _);

        var ok = fs.TryUpdateCapacity(1024);

        Assert.False(ok);

        // Capacity was left unchanged: growing back to the original value still succeeds,
        // and the existing file is still there (TryUpdateCapacity never touches NodeMap).
        Assert.True(fs.TryUpdateCapacity(1024 * 1024));
        Assert.True(fs.NodeMap.TryGet("\\big.bin", out _));
    }

    [Fact]
    public void TryUpdateCapacity_ReducingToExactlyCurrentUsage_Succeeds()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 4096,
            out _, out _, out _, out _);
        var used = fs.NodeMap.GetTotalAllocated();

        var ok = fs.TryUpdateCapacity(used);

        Assert.True(ok);
    }

    [Theory]
    [InlineData(ulong.MaxValue, false)]
    [InlineData(ulong.MaxValue, true)]
    [InlineData(1UL << 63, false)]
    [InlineData(1UL << 63, true)]
    public void SetFileSize_SizeFarBeyondCapacity_ReturnsDiskFullAndLeavesFileUnchanged(ulong newSize, bool setAllocationSize)
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        fs.SetFileSize(fileNode!, null!, 4096, setAllocationSize: false, out _);
        var node = (FileNode)fileNode!;
        var totalBefore = fs.NodeMap.GetTotalAllocated();

        var status = fs.SetFileSize(fileNode!, null!, newSize, setAllocationSize, out _);

        Assert.Equal(DiskFull, status);
        Assert.Equal(4096UL, node.FileInfo.FileSize);
        Assert.Equal(4096UL, node.FileInfo.AllocationSize);
        Assert.NotNull(node.FileData);
        Assert.Equal(totalBefore, fs.NodeMap.GetTotalAllocated());
    }

    [Fact]
    public void Create_AllocationSizeNearUlongMax_ReturnsDiskFull()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");

        var status = fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], ulong.MaxValue - 1,
            out _, out _, out _, out _);

        Assert.Equal(DiskFull, status);
        Assert.False(fs.NodeMap.TryGet("\\file.bin", out _));
    }

    [Fact]
    public void Overwrite_AllocationSizeNearUlongMax_ReturnsDiskFull()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        var status = fs.Overwrite(fileNode!, null!, (uint)FileAttributes.Normal, false, ulong.MaxValue - 1, out _);

        Assert.Equal(DiskFull, status);
        Assert.Equal(0UL, fs.NodeMap.GetTotalAllocated());
    }

    /// <summary>
    /// NTSTATUS <c>STATUS_DISK_FULL</c>.
    /// </summary>
    private const int DiskFull = unchecked((int)0xC000007F);
}
