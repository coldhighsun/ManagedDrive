namespace ManagedDrive.Tests;

public sealed class FileNodeMapStreamTests
{
    private static readonly Func<ulong> Unlimited = () => ulong.MaxValue;

    [Fact]
    public void TryCreate_StreamWithoutItsFile_ReturnsOwnerNotFound()
    {
        var map = NewMap();

        var result = map.TryCreate("\\a.txt:s", MakeFile(), Unlimited);

        Assert.Equal(FileNodeMap.CreateResult.OwnerNotFound, result);
        Assert.False(map.TryGet("\\a.txt:s", out _));
    }

    [Fact]
    public void TryCreate_StreamOfAnExistingFile_Creates()
    {
        var map = NewMap();
        map.Add("\\a.txt", MakeFile());

        var result = map.TryCreate("\\a.txt:s", MakeFile(), Unlimited);

        Assert.Equal(FileNodeMap.CreateResult.Created, result);
    }

    [Fact]
    public void GetChildren_DirectoryWithStreams_SkipsTheStreams()
    {
        var map = NewMap();
        map.Add("\\a.txt", MakeFile());
        map.Add("\\a.txt:s", MakeFile());
        map.Add("\\dir", MakeDir());
        map.Add("\\dir:note", MakeFile());
        map.Add("\\dir\\inner.txt", MakeFile());
        map.Add("\\dir\\inner.txt:s", MakeFile());

        var root = map.GetChildren("\\", null).Select(c => c.Key);
        var dir = map.GetChildren("\\dir", null).Select(c => c.Key);

        Assert.Equal(["\\a.txt", "\\dir"], root);
        Assert.Equal(["\\dir\\inner.txt"], dir);
    }

    [Fact]
    public void GetChildren_WithMarker_StillSkipsStreams()
    {
        var map = NewMap();
        map.Add("\\a.txt", MakeFile());
        map.Add("\\a.txt:s", MakeFile());
        map.Add("\\b.txt", MakeFile());
        map.Add("\\b.txt:s", MakeFile());

        var children = map.GetChildren("\\", "a.txt").Select(c => c.Key);

        Assert.Equal(["\\b.txt"], children);
    }

    [Fact]
    public void HasChildren_DirectoryWithOnlyStreams_ReturnsFalse()
    {
        var map = NewMap();
        map.Add("\\dir", MakeDir());
        map.Add("\\dir:note", MakeFile());

        Assert.False(map.HasChildren("\\dir"));
    }

    [Fact]
    public void HasChildren_DirectoryHoldingANodeWithAnUnreachableColonName_ReturnsTrue()
    {
        var map = NewMap();
        map.Add("\\dir", MakeDir());
        map.Add("\\dir\\C:", MakeFile());

        Assert.True(map.HasChildren("\\dir"));
        Assert.Empty(map.GetChildren("\\dir", null));
    }

    [Fact]
    public void TryDelete_DirectoryHoldingANodeWithAnUnreachableColonName_KeepsIt()
    {
        var map = NewMap();
        var dir = MakeDir();
        map.Add("\\dir", dir);
        map.Add("\\dir\\C:", MakeFile());

        var deleted = map.TryDelete("\\dir", dir);

        Assert.False(deleted);
        Assert.True(map.TryGet("\\dir\\C:", out _));
    }

    [Theory]
    [InlineData("\\a:s", true)]
    [InlineData("\\dir\\a.txt:Zone.Identifier", true)]
    [InlineData("\\a", false)]
    [InlineData("\\a:", false)]
    [InlineData("\\C:", false)]
    [InlineData("\\a:s:$DATA", false)]
    [InlineData("\\a:b\\c", false)]
    public void IsWellFormedStreamKey_ClassifiesKeys(string key, bool expected)
    {
        Assert.Equal(expected, AlternateStreamName.IsWellFormedStreamKey(key));
    }

    [Fact]
    public void TryDeleteIfUnused_EmptyFileWithoutStreams_RemovesIt()
    {
        var map = NewMap();
        var file = MakeFile();
        map.Add("\\a", file);

        Assert.True(map.TryDeleteIfUnused("\\a", file));
        Assert.False(map.TryGet("\\a", out _));
    }

    [Fact]
    public void TryDeleteIfUnused_FileGainedAStream_KeepsBoth()
    {
        var map = NewMap();
        var file = MakeFile();
        map.Add("\\a", file);
        map.Add("\\a:other", MakeFile());

        Assert.False(map.TryDeleteIfUnused("\\a", file));
        Assert.True(map.TryGet("\\a", out _));
        Assert.True(map.TryGet("\\a:other", out _));
    }

    [Fact]
    public void TryDeleteIfUnused_FileGainedContent_KeepsIt()
    {
        var map = NewMap();
        var file = MakeFile();
        map.Add("\\a", file);
        file.FileInfo.FileSize = 10;

        Assert.False(map.TryDeleteIfUnused("\\a", file));
    }

    [Fact]
    public void TryDeleteIfUnused_PathNowHoldsAnotherNode_KeepsIt()
    {
        var map = NewMap();
        var original = MakeFile();
        map.Add("\\a", MakeFile());

        Assert.False(map.TryDeleteIfUnused("\\a", original));
        Assert.True(map.TryGet("\\a", out _));
    }

    [Fact]
    public void GetStreams_ReturnsOnlyTheStreamsOfThatPathInOrder()
    {
        var map = NewMap();
        map.Add("\\a", MakeFile());
        map.Add("\\a:y", MakeFile());
        map.Add("\\a:x", MakeFile());
        map.Add("\\ab", MakeFile());
        map.Add("\\ab:z", MakeFile());
        map.Add("\\a\\child", MakeFile());

        var streams = map.GetStreams("\\a").Select(s => s.Key);

        Assert.Equal(["\\a:x", "\\a:y"], streams);
    }

    [Fact]
    public void GetStreams_PathWithoutStreams_ReturnsEmpty()
    {
        var map = NewMap();
        map.Add("\\a", MakeFile());

        Assert.Empty(map.GetStreams("\\a"));
    }

    [Fact]
    public void Remove_FileWithStreams_RemovesThemAndTheirAllocation()
    {
        var map = NewMap();
        map.Add("\\a", MakeFile());
        map.Add("\\a:s", MakeFile(allocation: 1024));
        map.Add("\\ab:s", MakeFile());

        map.Remove("\\a");

        Assert.False(map.TryGet("\\a:s", out _));
        Assert.True(map.TryGet("\\ab:s", out _));
        Assert.Equal(0UL, map.GetTotalAllocated());
    }

    [Fact]
    public void TryDelete_FileWithStreams_RemovesThem()
    {
        var map = NewMap();
        var file = MakeFile();
        map.Add("\\a", file);
        var stream = MakeFile(allocation: 512);
        map.Add("\\a:s", stream);

        var deleted = map.TryDelete("\\a", file);

        Assert.True(deleted);
        Assert.False(map.TryGet("\\a:s", out _));
        Assert.True(stream.IsDetached);
        Assert.Equal(0UL, map.GetTotalAllocated());
    }

    [Fact]
    public void TryDelete_Stream_LeavesItsFileAlone()
    {
        var map = NewMap();
        map.Add("\\a", MakeFile());
        var stream = MakeFile();
        map.Add("\\a:s", stream);

        var deleted = map.TryDelete("\\a:s", stream);

        Assert.True(deleted);
        Assert.True(map.TryGet("\\a", out _));
    }

    [Fact]
    public void TryDelete_DirectoryHoldingOnlyStreams_DeletesItWithThem()
    {
        var map = NewMap();
        var dir = MakeDir();
        map.Add("\\dir", dir);
        map.Add("\\dir:note", MakeFile());

        var deleted = map.TryDelete("\\dir", dir);

        Assert.True(deleted);
        Assert.Equal(1, map.Count);
        Assert.False(map.TryGet("\\dir:note", out _));
    }

    [Fact]
    public void RemoveSubtree_Directory_RemovesItsStreamsAndThoseOfItsFiles()
    {
        var map = NewMap();
        map.Add("\\dir", MakeDir());
        map.Add("\\dir:note", MakeFile());
        map.Add("\\dir\\f", MakeFile());
        map.Add("\\dir\\f:s", MakeFile());
        map.Add("\\other:s", MakeFile());

        map.RemoveSubtree("\\dir");

        Assert.Equal(["\\", "\\other:s"], map.GetAllNodes().Select(n => n.Key));
    }

    [Fact]
    public void Rename_FileWithStreams_MovesThemAndBumpsTheirMetadataVersion()
    {
        var map = NewMap();
        var file = MakeFile();
        map.Add("\\a", file);
        var stream = MakeFile();
        map.Add("\\a:s", stream);
        var versionBefore = stream.MetadataVersion;

        var conflict = map.Rename("\\a", "\\b", file, replaceIfExists: false);

        Assert.Equal(FileNodeMap.RenameConflict.None, conflict);
        Assert.Equal(["\\", "\\b", "\\b:s"], map.GetAllNodes().Select(n => n.Key));
        Assert.Equal(versionBefore + 1, stream.MetadataVersion);
    }

    [Fact]
    public void RemoveInvalidStreams_DropsOrphanAndDirectoryStreams()
    {
        var map = NewMap();
        map.Add("\\ok", MakeFile());
        map.Add("\\ok:fine", MakeFile());
        map.Add("\\orphan:s", MakeFile());
        map.Add("\\ok:dirstream", MakeDir());

        var removed = map.RemoveInvalidStreams();

        Assert.Equal(["\\ok:dirstream", "\\orphan:s"], removed.Order(StringComparer.Ordinal));
        Assert.Equal(["\\", "\\ok", "\\ok:fine"], map.GetAllNodes().Select(n => n.Key));
    }

    [Fact]
    public void RemoveInvalidStreams_NodesWithAColonThatAreNotStreamNames_AreKept()
    {
        var map = NewMap();
        map.Add("\\ok", MakeFile());
        map.Add("\\ok:with:$DATA", MakeFile());
        map.Add("\\ok:", MakeFile());
        map.Add("\\C:", MakeFile());
        map.Add("\\bad:dir\\child", MakeFile());

        var removed = map.RemoveInvalidStreams();

        Assert.Empty(removed);
        Assert.Equal(6, map.Count);
    }

    [Fact]
    public void RemoveInvalidStreams_MapWithoutProblems_RemovesNothing()
    {
        var map = NewMap();
        map.Add("\\a", MakeFile());
        map.Add("\\a:s", MakeFile());

        Assert.Empty(map.RemoveInvalidStreams());
        Assert.Equal(3, map.Count);
    }

    private static FileNodeMap NewMap()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        return map;
    }

    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    private static FileNode MakeFile(ulong allocation = 0) => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Normal, AllocationSize = allocation },
    };
}
