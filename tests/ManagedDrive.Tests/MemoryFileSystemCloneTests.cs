namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemCloneTests
{
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
