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

    /// <summary>
    /// A node loaded from an image keeps the index number it was saved with; files created
    /// afterwards must not be handed that same ID.
    /// </summary>
    [Fact]
    public void Add_LoadedNodeIndexNumber_IsNeverReissued()
    {
        var map = new FileNodeMap();
        var loaded = MakeFile();
        loaded.FileInfo.IndexNumber = FileNode.NewIndexNumber() + 1_000_000;

        map.Add("\\loaded.txt", loaded);

        Assert.True(FileNode.NewIndexNumber() > loaded.FileInfo.IndexNumber);
    }

    [Fact]
    public void Add_LowerIndexNumber_DoesNotMoveCounterBackwards()
    {
        var map = new FileNodeMap();
        var before = FileNode.NewIndexNumber();
        var node = MakeFile();
        node.FileInfo.IndexNumber = 1;

        map.Add("\\old.txt", node);

        Assert.True(FileNode.NewIndexNumber() > before);
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
    public void GetChildren_SiblingsSortingAroundSeparator_AllReturnedDescendantsExcluded()
    {
        // ' ' and '-' sort before '\', ']' and '_' sort after it (OrdinalIgnoreCase), so these
        // siblings interleave with the "\A\..." subtree in _sortedKeys.
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\A", MakeDir());
        map.Add("\\A B", MakeFile());
        map.Add("\\A-1", MakeFile());
        map.Add("\\A\\x", MakeFile());
        map.Add("\\A\\Deep", MakeDir());
        map.Add("\\A\\Deep\\y", MakeFile());
        map.Add("\\A]", MakeFile());
        map.Add("\\A_", MakeFile());
        map.Add("\\a2", MakeDir());
        map.Add("\\a2\\z", MakeFile());
        map.Add("\\B", MakeFile());

        var keys = map.GetChildren("\\", null).Select(kvp => kvp.Key).ToList();

        Assert.Equal(["\\A", "\\A B", "\\A-1", "\\a2", "\\A]", "\\A_", "\\B"], keys);
    }

    [Fact]
    public void GetChildren_PagedWithMarkers_VisitsEveryChildExactlyOnce()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\Dir", MakeDir());
        var expected = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var child = $"\\Dir\\c{i:D2}";
            expected.Add(child);
            map.Add(child, MakeDir());
            map.Add(child + "\\inner", MakeDir());
            map.Add(child + "\\inner\\leaf.txt", MakeFile());
        }

        var visited = new List<string>();
        string? marker = null;
        while (true)
        {
            var page = map.GetChildren("\\Dir", marker).Take(3).ToList();
            if (page.Count == 0)
            {
                break;
            }

            visited.AddRange(page.Select(kvp => kvp.Key));
            marker = page[^1].Value.LeafName;
        }

        Assert.Equal(expected, visited);
    }

    [Fact]
    public void GetChildren_MarkerIsDirectoryWithSubtree_SkipsItsDescendants()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\A", MakeDir());
        map.Add("\\A\\x", MakeFile());
        map.Add("\\A\\y", MakeFile());
        map.Add("\\B", MakeFile());

        var keys = map.GetChildren("\\", "a").Select(kvp => kvp.Key).ToList();

        Assert.Equal(["\\B"], keys);
    }

    [Fact]
    public void HasChildren_OnlyDeepDescendantsBeforeDirectChild_ReturnsTrue()
    {
        var map = new FileNodeMap();
        map.Add("\\Sub", MakeDir());
        map.Add("\\Sub\\A", MakeDir());
        map.Add("\\Sub\\A\\B", MakeDir());
        map.Add("\\Sub\\A\\B\\C", MakeFile());

        Assert.True(map.HasChildren("\\Sub"));
        Assert.True(map.HasChildren("\\Sub\\A"));
        Assert.False(map.HasChildren("\\Sub\\A\\B\\C"));
    }

    [Fact]
    public void HasChildren_EmptyDirectory_ReturnsFalse()
    {
        var map = new FileNodeMap();
        map.Add("\\Empty", MakeDir());

        Assert.False(map.HasChildren("\\Empty"));
    }

    [Fact]
    public void HasChildren_NonEmptyDirectory_ReturnsTrue()
    {
        var map = new FileNodeMap();
        map.Add("\\Sub", MakeDir());
        map.Add("\\Sub\\File.txt", MakeFile());

        Assert.True(map.HasChildren("\\Sub"));
    }

    [Fact]
    public void HasChildren_SimilarlyNamedSiblingDirectory_DoesNotCountAsChild()
    {
        var map = new FileNodeMap();
        map.Add("\\Sub", MakeDir());
        map.Add("\\SubOther", MakeDir());
        map.Add("\\SubOther\\File.txt", MakeFile());

        Assert.False(map.HasChildren("\\Sub"));
        Assert.True(map.HasChildren("\\SubOther"));
    }

    [Fact]
    public void HasChildren_RootDirectory_Works()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());

        Assert.False(map.HasChildren("\\"));

        map.Add("\\A", MakeFile());

        Assert.True(map.HasChildren("\\"));
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

    [Fact]
    public async Task ConcurrentReadersAndWriters_DoNotCorruptState()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());

        // Seed a stable set of files that readers will look up throughout the run.
        for (var i = 0; i < 100; i++)
        {
            var node = MakeFile();
            node.FileInfo.AllocationSize = 512;
            map.Add($"\\stable{i}.txt", node);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        void Guard(Action body)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    body();
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        }

        // Guard() already exits its own loop once the token is canceled; passing the same token as
        // Task.Run's scheduling token is redundant and actively harmful - if the thread pool is slow
        // to start a task before the 2s deadline (e.g. a loaded CI runner), Task.Run throws
        // TaskCanceledException before Guard ever runs, which Task.WhenAll then propagates
        // unguarded, failing the test despite no actual corruption.
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() => Guard(() =>
        {
            map.TryGet("\\stable50.txt", out _);
            _ = map.GetChildren("\\", null).Count();
            _ = map.GetTotalAllocated();
            _ = map.Count;
        })));

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() => Guard(() =>
        {
            var path = $"\\churn{w}.txt";
            var node = MakeFile();
            node.FileInfo.AllocationSize = 1024;
            map.Add(path, node);
            map.UpdateAllocationSize(node, 2048);
            map.Remove(path);
        })));

        await Task.WhenAll([.. readers, .. writers]);

        Assert.Empty(exceptions);

        // The stable set (root + 100 files) must be intact and its total unchanged.
        Assert.Equal(101, map.Count);
        Assert.Equal(100UL * 512, map.GetTotalAllocated());
    }

    [Fact]
    public void RenameDescendants_BumpsMetadataVersionOfDescendants()
    {
        var map = new FileNodeMap();
        var file = MakeFile();
        map.Add("\\Old\\file.txt", file);

        map.RenameDescendants("\\Old", "\\New");

        Assert.Equal(1UL, file.MetadataVersion);
    }

    [Fact]
    public void UpdateAllocationSize_NodeRemovedByClearAll_LeavesTotalUnchanged()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        var stale = MakeFile();
        stale.FileInfo.AllocationSize = 4096;
        map.Add("\\stale.bin", stale);
        map.ClearAll();

        map.UpdateAllocationSize(stale, 0);

        Assert.Equal(0UL, map.GetTotalAllocated());
        Assert.Equal(0UL, stale.FileInfo.AllocationSize);
    }

    [Fact]
    public void TryUpdateAllocationSizeWithinCapacity_DetachedNodeGrowing_ReturnsFalseAndLeavesTotalUnchanged()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        var stale = MakeFile();
        map.Add("\\stale.bin", stale);
        map.Remove("\\stale.bin");

        var applied = map.TryUpdateAllocationSizeWithinCapacity(stale, 4096, ulong.MaxValue);

        Assert.False(applied);
        Assert.Equal(0UL, map.GetTotalAllocated());
        Assert.Equal(0UL, stale.FileInfo.AllocationSize);
    }

    [Fact]
    public void TryUpdateAllocationSizeWithinCapacity_SizeAtLeast2Pow63_ReturnsFalseAndLeavesTotalUnchanged()
    {
        var map = new FileNodeMap();
        var file = MakeFile();
        file.FileInfo.AllocationSize = 4096;
        map.Add("\\f", file);

        var applied = map.TryUpdateAllocationSizeWithinCapacity(file, 1UL << 63, 1024 * 1024);

        Assert.False(applied);
        Assert.Equal(4096UL, file.FileInfo.AllocationSize);
        Assert.Equal(4096UL, map.GetTotalAllocated());
    }

    [Fact]
    public void UpdateAllocationSize_NodeReaddedByRename_CountsTowardTotal()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        var file = MakeFile();
        map.Add("\\a.bin", file);
        map.Rename("\\a.bin", "\\b.bin", file, replaceIfExists: false);

        map.UpdateAllocationSize(file, 4096);

        Assert.Equal(4096UL, map.GetTotalAllocated());
    }

    [Fact]
    public void UpdateAllocationSize_NodeReplacedAtSamePath_LeavesTotalUnchanged()
    {
        var map = new FileNodeMap();
        var old = MakeFile();
        old.FileInfo.AllocationSize = 512;
        map.Add("\\f", old);
        var replacement = MakeFile();
        replacement.FileInfo.AllocationSize = 1024;
        map.Add("\\f", replacement);

        map.UpdateAllocationSize(old, 0);

        Assert.Equal(1024UL, map.GetTotalAllocated());
    }

    [Fact]
    public void ReplaceAll_NewNodes_StoresThemAndCountsOnlyTheirAllocation()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        var old = MakeFile();
        old.FileInfo.AllocationSize = 512;
        map.Add("\\old.bin", old);
        var incoming = MakeFile();
        incoming.FileInfo.AllocationSize = 1024;

        map.ReplaceAll([KeyValuePair.Create("\\", MakeDir()), KeyValuePair.Create("\\new.bin", incoming)]);
        map.UpdateAllocationSize(old, 0);

        Assert.False(map.TryGet("\\old.bin", out _));
        Assert.True(map.TryGet("\\new.bin", out var stored));
        Assert.Same(incoming, stored);
        Assert.Equal(2, map.Count);
        Assert.Equal(1024UL, map.GetTotalAllocated());
    }

    [Fact]
    public void ReplaceAll_WithoutRoot_KeepsCurrentRoot()
    {
        var map = new FileNodeMap();
        var root = MakeDir();
        map.Add("\\", root);

        map.ReplaceAll([KeyValuePair.Create("\\a.bin", MakeFile())]);

        Assert.True(map.TryGet("\\", out var stored));
        Assert.Same(root, stored);
    }

    [Fact]
    public void TryDelete_ExpectedNodeAtPath_RemovesIt()
    {
        var map = new FileNodeMap();
        var file = MakeFile();
        map.Add("\\f", file);

        var removed = map.TryDelete("\\f", file);

        Assert.True(removed);
        Assert.False(map.TryGet("\\f", out _));
    }

    [Fact]
    public void TryDelete_DifferentNodeAtPath_KeepsIt()
    {
        var map = new FileNodeMap();
        var stale = MakeFile();
        map.Add("\\f", stale);
        map.ClearAll();
        var current = MakeFile();
        map.Add("\\f", current);

        var removed = map.TryDelete("\\f", stale);

        Assert.False(removed);
        Assert.True(map.TryGet("\\f", out var stored));
        Assert.Same(current, stored);
    }

    [Fact]
    public void TryDelete_DirectoryThatGainedAChild_KeepsIt()
    {
        var map = new FileNodeMap();
        var dir = MakeDir();
        map.Add("\\d", dir);
        map.Add("\\d\\child.txt", MakeFile());

        var removed = map.TryDelete("\\d", dir);

        Assert.False(removed);
        Assert.True(map.TryGet("\\d", out _));
    }

    [Fact]
    public void TryDelete_EmptyDirectory_RemovesIt()
    {
        var map = new FileNodeMap();
        var dir = MakeDir();
        map.Add("\\d", dir);

        var removed = map.TryDelete("\\d", dir);

        Assert.True(removed);
        Assert.False(map.TryGet("\\d", out _));
    }

    [Fact]
    public void TryCreate_ParentMissing_ReturnsParentNotFoundAndAddsNothing()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());

        var result = map.TryCreate("\\gone\\child.txt", MakeFile(), ulong.MaxValue);

        Assert.Equal(FileNodeMap.CreateResult.ParentNotFound, result);
        Assert.Equal(1, map.Count);
    }

    [Fact]
    public void TryCreate_ParentIsFile_ReturnsParentNotDirectory()
    {
        var map = new FileNodeMap();
        map.Add("\\file.txt", MakeFile());

        var result = map.TryCreate("\\file.txt\\child.txt", MakeFile(), ulong.MaxValue);

        Assert.Equal(FileNodeMap.CreateResult.ParentNotDirectory, result);
    }

    [Fact]
    public void TryCreate_PathAlreadyExists_ReturnsNameCollisionAndKeepsExistingNode()
    {
        var map = new FileNodeMap();
        var existing = MakeFile();
        map.Add("\\f", existing);

        var result = map.TryCreate("\\f", MakeFile(), ulong.MaxValue);

        Assert.Equal(FileNodeMap.CreateResult.NameCollision, result);
        Assert.True(map.TryGet("\\f", out var stored));
        Assert.Same(existing, stored);
    }

    [Fact]
    public void TryCreate_ExceedingCapacity_ReturnsCapacityExceeded()
    {
        var map = new FileNodeMap();
        var node = MakeFile();
        node.FileInfo.AllocationSize = 8192;

        var result = map.TryCreate("\\f", node, 4096);

        Assert.Equal(FileNodeMap.CreateResult.CapacityExceeded, result);
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void TryCreate_UnderExistingDirectory_AddsNode()
    {
        var map = new FileNodeMap();
        map.Add("\\d", MakeDir());
        var node = MakeFile();

        var result = map.TryCreate("\\d\\f", node, ulong.MaxValue);

        Assert.Equal(FileNodeMap.CreateResult.Created, result);
        Assert.True(map.TryGet("\\d\\f", out var stored));
        Assert.Same(node, stored);
    }

    [Fact]
    public void Rename_NodeReplacedAtSourcePath_ReturnsSourceNotFoundAndKeepsNewNode()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        var stale = MakeFile();
        map.Add("\\a.txt", stale);
        map.ReplaceAll([KeyValuePair.Create("\\", MakeDir()), KeyValuePair.Create("\\a.txt", MakeFile())]);
        map.TryGet("\\a.txt", out var current);

        var result = map.Rename("\\a.txt", "\\b.txt", stale, replaceIfExists: false);

        Assert.Equal(FileNodeMap.RenameConflict.SourceNotFound, result);
        Assert.True(map.TryGet("\\a.txt", out var stored));
        Assert.Same(current, stored);
        Assert.False(map.TryGet("\\b.txt", out _));
    }

    [Fact]
    public void Rename_DestinationParentMissing_ReturnsParentNotFoundAndLeavesNodeInPlace()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        var file = MakeFile();
        map.Add("\\x", file);

        var result = map.Rename("\\x", "\\gone\\x", file, replaceIfExists: false);

        Assert.Equal(FileNodeMap.RenameConflict.ParentNotFound, result);
        Assert.True(map.TryGet("\\x", out var stored));
        Assert.Same(file, stored);
        Assert.False(map.TryGet("\\gone\\x", out _));
    }

    [Fact]
    public void Rename_DestinationParentIsFile_ReturnsParentNotDirectory()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\file.txt", MakeFile());
        var file = MakeFile();
        map.Add("\\x", file);

        var result = map.Rename("\\x", "\\file.txt\\x", file, replaceIfExists: false);

        Assert.Equal(FileNodeMap.RenameConflict.ParentNotDirectory, result);
        Assert.True(map.TryGet("\\x", out _));
    }

    [Fact]
    public void Rename_IntoExistingDirectory_MovesNode()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\dir", MakeDir());
        var file = MakeFile();
        map.Add("\\x", file);

        var result = map.Rename("\\x", "\\dir\\x", file, replaceIfExists: false);

        Assert.Equal(FileNodeMap.RenameConflict.None, result);
        Assert.True(map.TryGet("\\dir\\x", out var stored));
        Assert.Same(file, stored);
        Assert.False(map.TryGet("\\x", out _));
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
