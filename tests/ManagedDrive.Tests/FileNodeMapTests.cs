namespace ManagedDrive.Tests;

public sealed class FileNodeMapTests
{
    [Fact]
    public void Add_Root_SetsLeafNameToEmpty()
    {
        var map = new FileNodeMap();
        var node = MakeDir();
        map.Add("\\", node);

        Assert.Equal(string.Empty, node.LeafName);
    }

    [Fact]
    public void Add_SetsLeafName()
    {
        var map = new FileNodeMap();
        var node = MakeFile();
        map.Add("\\Folder\\file.txt", node);

        Assert.Equal("file.txt", node.LeafName);
    }

    [Fact]
    public void Add_UpdatesFilePathOnNode()
    {
        var map = new FileNodeMap();
        var node = new FileNode();
        map.Add("\\Folder\\file.txt", node);

        Assert.Equal("\\Folder\\file.txt", node.FilePath);
    }

    [Fact]
    public void Count_ReflectsAddAndRemove()
    {
        var map = new FileNodeMap();
        Assert.Equal(0, map.Count);

        map.Add("\\a", new());
        Assert.Equal(1, map.Count);

        map.Add("\\b", new());
        Assert.Equal(2, map.Count);

        map.Remove("\\a");
        Assert.Equal(1, map.Count);
    }

    [Fact]
    public void GetChildren_EmptyDir_ReturnsEmpty()
    {
        var map = new FileNodeMap();
        map.Add("\\Empty", MakeDir());

        var children = map.GetChildren("\\Empty", null).ToList();

        Assert.Empty(children);
    }

    [Fact]
    public void GetChildren_LeafNameMatchesChildKeySuffix()
    {
        var map = new FileNodeMap();
        map.Add("\\Sub", MakeDir());
        map.Add("\\Sub\\File.txt", MakeFile());

        var child = map.GetChildren("\\Sub", null).Single();

        Assert.Equal("File.txt", child.Value.LeafName);
    }

    [Fact]
    public void GetChildren_RootDir_ReturnsOnlyImmediateChildren()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\A", MakeFile());
        map.Add("\\B", MakeFile());
        map.Add("\\A\\deep", MakeFile());

        var children = map.GetChildren("\\", null).ToList();

        Assert.Equal(2, children.Count);
        Assert.Contains(children, kvp => kvp.Key == "\\A");
        Assert.Contains(children, kvp => kvp.Key == "\\B");
    }

    [Fact]
    public void GetChildren_SubDir_ReturnsOnlyDirectChildren()
    {
        var map = new FileNodeMap();
        map.Add("\\Sub", MakeDir());
        map.Add("\\Sub\\File.txt", MakeFile());
        map.Add("\\Sub\\Nested", MakeDir());
        map.Add("\\Sub\\Nested\\deep.txt", MakeFile());

        var children = map.GetChildren("\\Sub", null).ToList();

        Assert.Equal(2, children.Count);
        Assert.Contains(children, kvp => kvp.Key == "\\Sub\\File.txt");
        Assert.Contains(children, kvp => kvp.Key == "\\Sub\\Nested");
    }

    [Fact]
    public void GetChildren_WithMarker_SkipsUpToAndIncludingMarker()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\A", MakeFile());
        map.Add("\\B", MakeFile());
        map.Add("\\C", MakeFile());

        // Marker is "A" → return only B and C
        var children = map.GetChildren("\\", "A").ToList();

        Assert.Equal(2, children.Count);
        Assert.DoesNotContain(children, kvp => kvp.Key == "\\A");
    }

    [Fact]
    public void GetTotalAllocated_AfterClearAll_ExcludesRemovedNodes()
    {
        var map = new FileNodeMap();

        var root = MakeDir();
        root.FileInfo.AllocationSize = 0;
        map.Add("\\", root);

        var f1 = MakeFile();
        f1.FileInfo.AllocationSize = 512;
        map.Add("\\f1", f1);

        map.ClearAll();

        Assert.Equal(0UL, map.GetTotalAllocated());
    }

    [Fact]
    public void GetTotalAllocated_AfterRemove_SubtractsAllocationSize()
    {
        var map = new FileNodeMap();

        var f1 = MakeFile();
        f1.FileInfo.AllocationSize = 512;
        var f2 = MakeFile();
        f2.FileInfo.AllocationSize = 1024;
        map.Add("\\f1", f1);
        map.Add("\\f2", f2);

        map.Remove("\\f1");

        Assert.Equal(1024UL, map.GetTotalAllocated());
    }

    [Fact]
    public void GetTotalAllocated_AfterReplacingExistingKey_ReplacesOldSize()
    {
        var map = new FileNodeMap();

        var f1 = MakeFile();
        f1.FileInfo.AllocationSize = 512;
        map.Add("\\f1", f1);

        var replacement = MakeFile();
        replacement.FileInfo.AllocationSize = 2048;
        map.Add("\\f1", replacement);

        Assert.Equal(2048UL, map.GetTotalAllocated());
    }

    [Fact]
    public void GetTotalAllocated_EmptyMap_ReturnsZero()
    {
        var map = new FileNodeMap();
        Assert.Equal(0UL, map.GetTotalAllocated());
    }

    [Fact]
    public void GetTotalAllocated_SumsAllocationSizes()
    {
        var map = new FileNodeMap();

        var f1 = MakeFile();
        f1.FileInfo.AllocationSize = 512;
        var f2 = MakeFile();
        f2.FileInfo.AllocationSize = 1024;
        map.Add("\\f1", f1);
        map.Add("\\f2", f2);

        Assert.Equal(1536UL, map.GetTotalAllocated());
    }

    [Fact]
    public void Remove_ExistingPath_NodeGone()
    {
        var map = new FileNodeMap();
        var node = new FileNode();
        map.Add("\\file.txt", node);
        map.Remove("\\file.txt");

        var found = map.TryGet("\\file.txt", out _);

        Assert.False(found);
    }

    [Fact]
    public void Remove_NonExistingPath_DoesNotThrow()
    {
        var map = new FileNodeMap();
        map.Remove("\\ghost.txt");
    }

    [Fact]
    public void RenameDescendants_UpdatesAllDescendantPaths()
    {
        var map = new FileNodeMap();
        map.Add("\\Old", MakeDir());
        map.Add("\\Old\\File.txt", MakeFile());
        map.Add("\\Old\\Sub", MakeDir());

        map.RenameDescendants("\\Old", "\\New");

        Assert.True(map.TryGet("\\New\\File.txt", out _));
        Assert.True(map.TryGet("\\New\\Sub", out _));
        Assert.False(map.TryGet("\\Old\\File.txt", out _));
        Assert.False(map.TryGet("\\Old\\Sub", out _));
    }

    [Fact]
    public void RenameDescendants_UpdatesFilePathProperty()
    {
        var map = new FileNodeMap();
        var file = MakeFile();
        map.Add("\\Old\\file.txt", file);

        map.RenameDescendants("\\Old", "\\New");

        Assert.Equal("\\New\\file.txt", file.FilePath);
    }

    [Fact]
    public void RenameDescendants_UpdatesLeafNameOfDescendants()
    {
        var map = new FileNodeMap();
        var file = MakeFile();
        map.Add("\\Old\\Sub\\file.txt", file);

        map.RenameDescendants("\\Old", "\\New");

        Assert.Equal("file.txt", file.LeafName);
        Assert.Equal("\\New\\Sub\\file.txt", file.FilePath);
    }

    [Fact]
    public void TryGet_AfterAdd_ReturnsNode()
    {
        var map = new FileNodeMap();
        var node = new FileNode();
        map.Add("\\file.txt", node);

        var found = map.TryGet("\\file.txt", out var result);

        Assert.True(found);
        Assert.Same(node, result);
    }

    [Fact]
    public void TryGet_CaseInsensitive_ReturnsNode()
    {
        var map = new FileNodeMap();
        var node = new FileNode();
        map.Add("\\File.TXT", node);

        var found = map.TryGet("\\file.txt", out var result);

        Assert.True(found);
        Assert.Same(node, result);
    }

    [Fact]
    public void TryGet_MissingPath_ReturnsFalse()
    {
        var map = new FileNodeMap();

        var found = map.TryGet("\\nonexistent.txt", out _);

        Assert.False(found);
    }

    [Fact]
    public void UpdateAllocationSize_AdjustsCachedTotal()
    {
        var map = new FileNodeMap();

        var f1 = MakeFile();
        f1.FileInfo.AllocationSize = 512;
        map.Add("\\f1", f1);

        map.UpdateAllocationSize(f1, 2048);

        Assert.Equal(2048UL, f1.FileInfo.AllocationSize);
        Assert.Equal(2048UL, map.GetTotalAllocated());
    }

    [Fact]
    public void UpdateAllocationSize_MultipleNodes_KeepsTotalAccurate()
    {
        var map = new FileNodeMap();

        var f1 = MakeFile();
        f1.FileInfo.AllocationSize = 512;
        var f2 = MakeFile();
        f2.FileInfo.AllocationSize = 1024;
        map.Add("\\f1", f1);
        map.Add("\\f2", f2);

        map.UpdateAllocationSize(f1, 256);
        map.UpdateAllocationSize(f2, 4096);

        Assert.Equal(4352UL, map.GetTotalAllocated());
    }

    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    private static FileNode MakeFile() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Normal },
    };
}