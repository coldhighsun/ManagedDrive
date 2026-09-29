using System.Runtime.InteropServices;

namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemCloneTests
{
    [Fact]
    public void TryReplaceContents_CopyBelowLowMemoryReserve_ReturnsFalseAndLeavesTargetUnchanged()
    {
        var source = new MemoryFileSystem(1024 * 1024, "Source");
        source.Create("\\data.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var sourceFileNode, out _, out _, out _);
        WriteBytes(source, sourceFileNode!, new byte[64 * 1024]);

        var target = new MemoryFileSystem(1024 * 1024, "Target", availableMemoryProvider: () => 0);
        target.Create("\\keep.txt", 0, 0, (uint)FileAttributes.Normal, [], 0, out _, out _, out _, out _);

        var ok = target.TryReplaceContents(source.NodeMap, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.True(target.NodeMap.TryGet("\\keep.txt", out _));
        Assert.False(target.NodeMap.TryGet("\\data.bin", out _));
    }

    [Fact]
    public void TryReplaceContents_SparseSourceBelowLowMemoryReserve_Succeeds()
    {
        // Preallocated-but-unwritten content clones sparse, so copying it allocates nothing.
        var source = new MemoryFileSystem(1024 * 1024, "Source");
        source.Create("\\sparse.bin", 0, 0, (uint)FileAttributes.Normal, [], 512 * 1024,
            out _, out _, out _, out _);

        var target = new MemoryFileSystem(1024 * 1024, "Target", availableMemoryProvider: () => 0);

        Assert.True(target.TryReplaceContents(source.NodeMap, out _));
        Assert.True(target.NodeMap.TryGet("\\sparse.bin", out _));
    }

    [Fact]
    public void TryReplaceContents_AdoptingNodes_TakesThemOverWithoutCopyingOrChargingMemory()
    {
        // A freshly loaded map (e.g. a snapshot being restored) has no other owner, so its nodes
        // are moved in as-is instead of being copied a second time.
        var loaded = new MemoryFileSystem(1024 * 1024, "Loaded");
        loaded.Create("\\data.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var loadedFileNode, out _, out _, out _);
        WriteBytes(loaded, loadedFileNode!, new byte[64 * 1024]);

        var target = new MemoryFileSystem(1024 * 1024, "Target", availableMemoryProvider: () => 0);

        var ok = target.TryReplaceContents(loaded.NodeMap, out _, adoptNodes: true);

        Assert.True(ok);
        Assert.True(target.NodeMap.TryGet("\\data.bin", out var adopted));
        Assert.Same(loadedFileNode, adopted);
        Assert.True(target.IsDirty);
    }

    private static void WriteBytes(MemoryFileSystem fs, object fileNode, byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            Assert.Equal(0, fs.Write(fileNode, null!, ptr, 0, (uint)data.Length, false, false, out _, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryReplaceContents_CopiesSourceNodes()
    {
        var source = new MemoryFileSystem(1024 * 1024, "Source");
        source.Create("\\file.txt", 0, 0, (uint)FileAttributes.Normal, [], 512,
            out _, out _, out _, out _);

        var target = new MemoryFileSystem(1024 * 1024, "Target");

        var ok = target.TryReplaceContents(source.NodeMap, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(target.NodeMap.TryGet("\\file.txt", out _));
    }

    [Fact]
    public void TryReplaceContents_DiscardsExistingTargetContents()
    {
        var source = new MemoryFileSystem(1024 * 1024, "Source");

        var target = new MemoryFileSystem(1024 * 1024, "Target");
        target.Create("\\stale.txt", 0, 0, (uint)FileAttributes.Normal, [], 512,
            out _, out _, out _, out _);

        var ok = target.TryReplaceContents(source.NodeMap, out _);

        Assert.True(ok);
        Assert.False(target.NodeMap.TryGet("\\stale.txt", out _));
    }

    [Fact]
    public void TryReplaceContents_ClonedNodesAreIndependentOfSource()
    {
        var source = new MemoryFileSystem(1024 * 1024, "Source");
        source.Create("\\file.txt", 0, 0, (uint)FileAttributes.Normal, [], 512,
            out var sourceFileNode, out _, out _, out _);

        var target = new MemoryFileSystem(1024 * 1024, "Target");
        target.TryReplaceContents(source.NodeMap, out _);

        target.NodeMap.TryGet("\\file.txt", out var targetFile);
        Assert.NotSame(sourceFileNode, targetFile);
    }

    [Fact]
    public void TryReplaceContents_TargetTooSmall_ReturnsFalseAndLeavesTargetUnchanged()
    {
        var source = new MemoryFileSystem(1024 * 1024, "Source");
        source.Create("\\big.bin", 0, 0, (uint)FileAttributes.Normal, [], 4096,
            out _, out _, out _, out _);

        var target = new MemoryFileSystem(1024, "Target");

        var ok = target.TryReplaceContents(source.NodeMap, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.False(target.NodeMap.TryGet("\\big.bin", out _));
    }

    [Fact]
    public void TryReplaceContents_ReadOnlyTarget_ReturnsFalse()
    {
        var source = new MemoryFileSystem(1024 * 1024, "Source");
        var target = new MemoryFileSystem(1024 * 1024, "Target", readOnly: true);

        var ok = target.TryReplaceContents(source.NodeMap, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
