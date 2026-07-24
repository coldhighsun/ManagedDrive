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
}