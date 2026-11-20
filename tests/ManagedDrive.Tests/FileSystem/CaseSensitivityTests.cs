namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the case-sensitive name comparison of <see cref="FileNodeMap"/>, the conversion between
/// the two modes, wildcard matching and the image round trip of names that differ only by case.
/// </summary>
public sealed class CaseSensitivityTests : IDisposable
{
    /// <summary>
    /// Capacity used for the images these tests save.
    /// </summary>
    private const ulong Capacity = 4UL * 1024 * 1024;

    /// <summary>
    /// Scratch directory the images are written to.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    /// <summary>
    /// Creates the scratch directory.
    /// </summary>
    public CaseSensitivityTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Removes the scratch directory.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// A default map ignores case, as before the option existed.
    /// </summary>
    [Fact]
    public void Constructor_Default_IsCaseInsensitive()
    {
        var map = new FileNodeMap();

        Assert.False(map.CaseSensitive);
        Assert.Equal(StringComparison.OrdinalIgnoreCase, map.Comparison);
    }

    /// <summary>
    /// In case-sensitive mode a lookup with different case finds nothing.
    /// </summary>
    [Fact]
    public void TryGet_CaseSensitiveMapDifferentCase_ReturnsFalse()
    {
        var map = new FileNodeMap(caseSensitive: true);
        map.Add("\\File.TXT", MakeFile());

        Assert.False(map.TryGet("\\file.txt", out _));
        Assert.True(map.TryGet("\\File.TXT", out _));
    }

    /// <summary>
    /// Two names that differ only by case are two entries and keep their own nodes.
    /// </summary>
    [Fact]
    public void Add_CaseSensitiveMapNamesDifferingByCase_KeepsBothNodes()
    {
        var map = new FileNodeMap(caseSensitive: true);
        var upper = MakeFile();
        var lower = MakeFile();

        map.Add("\\A.txt", upper);
        map.Add("\\a.txt", lower);

        Assert.True(map.TryGet("\\A.txt", out var foundUpper));
        Assert.True(map.TryGet("\\a.txt", out var foundLower));
        Assert.Same(upper, foundUpper);
        Assert.Same(lower, foundLower);
    }

    /// <summary>
    /// Creating a name that differs only by case from an existing one succeeds on a case-sensitive map.
    /// </summary>
    [Fact]
    public void TryCreate_CaseSensitiveMapOtherCase_Succeeds()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        map.Add("\\A.txt", MakeFile());

        var result = map.TryCreate("\\a.txt", MakeFile(), () => ulong.MaxValue);

        Assert.Equal(FileNodeMap.CreateResult.Created, result);
    }

    /// <summary>
    /// The same creation collides on a case-insensitive map.
    /// </summary>
    [Fact]
    public void TryCreate_CaseInsensitiveMapOtherCase_Collides()
    {
        var map = MakeMapWithRoot(caseSensitive: false);
        map.Add("\\A.txt", MakeFile());

        var result = map.TryCreate("\\a.txt", MakeFile(), () => ulong.MaxValue);

        Assert.Equal(FileNodeMap.CreateResult.NameCollision, result);
    }

    /// <summary>
    /// A directory listing of a case-sensitive map returns both case variants.
    /// </summary>
    [Fact]
    public void GetChildren_CaseSensitiveMap_ListsBothCaseVariants()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        map.Add("\\A.txt", MakeFile());
        map.Add("\\a.txt", MakeFile());
        map.Add("\\b.txt", MakeFile());

        var names = map.GetChildren("\\", null).Select(c => c.Value.LeafName).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["A.txt", "a.txt", "b.txt"], names);
    }

    /// <summary>
    /// Paging with a marker still visits every child exactly once on a case-sensitive map.
    /// </summary>
    [Fact]
    public void GetChildren_CaseSensitiveMapPagedWithMarkers_VisitsEveryChildOnce()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        foreach (var name in new[] { "A", "a", "B", "b", "c" })
        {
            map.Add($"\\{name}", MakeFile());
        }

        var seen = new List<string>();
        string? marker = null;
        while (true)
        {
            var page = map.GetChildren("\\", marker);
            if (page.Count == 0)
            {
                break;
            }

            var first = page[0].Value.LeafName;
            seen.Add(first);
            marker = first;
        }

        Assert.Equal(["A", "B", "a", "b", "c"], seen);
    }

    /// <summary>
    /// Only exceptions created as case collisions are recognized as such.
    /// </summary>
    [Fact]
    public void IsCaseCollision_PlainInvalidDataException_ReturnsFalse()
    {
        Assert.False(CaseCollision.IsCaseCollision(new InvalidDataException("corrupt")));
        Assert.False(CaseCollision.IsCaseCollision(new IOException("io")));
        Assert.True(CaseCollision.IsCaseCollision(CaseCollision.CreateException("clash")));
    }

    /// <summary>
    /// A case collision carries a marker, so callers can tell it from a corrupt file without matching the message.
    /// </summary>
    [Fact]
    public void ReplaceAll_CollisionOnCaseInsensitiveMap_IsMarkedAsCaseCollision()
    {
        var map = MakeMapWithRoot(caseSensitive: false);

        var ex = Assert.Throws<InvalidDataException>(() => map.ReplaceAll(
        [
            new("\\A.txt", MakeFile()),
            new("\\a.txt", MakeFile()),
        ]));

        Assert.True(CaseCollision.IsCaseCollision(ex));
    }

    /// <summary>
    /// A replacement that fails on a case collision leaves the map exactly as it was, instead of
    /// clearing it and stopping partway.
    /// </summary>
    [Fact]
    public void ReplaceAll_CollisionOnCaseInsensitiveMap_LeavesContentsUntouched()
    {
        var map = MakeMapWithRoot(caseSensitive: false);
        var keep = MakeFile(1024);
        map.Add("\\keep.txt", keep);
        var allocatedBefore = map.GetTotalAllocated();

        Assert.Throws<InvalidDataException>(() => map.ReplaceAll(
        [
            new("\\new.txt", MakeFile()),
            new("\\A.txt", MakeFile()),
            new("\\a.txt", MakeFile()),
        ]));

        Assert.True(map.TryGet("\\keep.txt", out var found));
        Assert.Same(keep, found);
        Assert.False(map.TryGet("\\new.txt", out _));
        Assert.Equal(allocatedBefore, map.GetTotalAllocated());
    }

    /// <summary>
    /// A replacement without collisions swaps the contents and keeps the allocation total right.
    /// </summary>
    [Fact]
    public void ReplaceAll_NoCollision_ReplacesContentsAndKeepsAllocation()
    {
        var map = MakeMapWithRoot(caseSensitive: false);
        map.Add("\\old.txt", MakeFile(512));

        map.ReplaceAll(
        [
            new("\\A.txt", MakeFile(1024)),
            new("\\b.txt", MakeFile(2048)),
        ]);

        Assert.False(map.TryGet("\\old.txt", out _));
        Assert.True(map.TryGet("\\a.TXT", out _));
        Assert.Equal(FileNode.AlignToAllocationUnit(1024) + FileNode.AlignToAllocationUnit(2048), map.GetTotalAllocated());
    }

    /// <summary>
    /// The same path twice is a plain replacement, not a case collision.
    /// </summary>
    [Fact]
    public void ReplaceAll_SamePathTwice_LastNodeWins()
    {
        var map = MakeMapWithRoot(caseSensitive: false);
        var second = MakeFile();

        map.ReplaceAll([new("\\x.txt", MakeFile()), new("\\x.txt", second)]);

        Assert.True(map.TryGet("\\x.txt", out var found));
        Assert.Same(second, found);
    }

    /// <summary>
    /// A replacement into a case-sensitive map accepts names that differ only by case.
    /// </summary>
    [Fact]
    public void ReplaceAll_CaseSensitiveMapWithCaseVariantNames_KeepsBoth()
    {
        var map = MakeMapWithRoot(caseSensitive: true);

        map.ReplaceAll([new("\\A.txt", MakeFile()), new("\\a.txt", MakeFile())]);

        Assert.True(map.TryGet("\\A.txt", out _));
        Assert.True(map.TryGet("\\a.txt", out _));
    }

    /// <summary>
    /// Two names differing only by case are reported as one pair, the earlier path first.
    /// </summary>
    [Fact]
    public void FindCaseCollisions_NamesDifferingByCase_ReturnsThePair()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        map.Add("\\A.txt", MakeFile());
        map.Add("\\a.txt", MakeFile());
        map.Add("\\b.txt", MakeFile());

        var collisions = map.FindCaseCollisions(5);

        Assert.Equal([("\\A.txt", "\\a.txt")], collisions);
    }

    /// <summary>
    /// Distinct names produce no collisions.
    /// </summary>
    [Fact]
    public void FindCaseCollisions_NoCollidingNames_ReturnsEmpty()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        map.Add("\\A.txt", MakeFile());
        map.Add("\\b.txt", MakeFile());

        Assert.Empty(map.FindCaseCollisions(5));
    }

    /// <summary>
    /// A case-insensitive map cannot hold such pairs, so nothing is reported.
    /// </summary>
    [Fact]
    public void FindCaseCollisions_CaseInsensitiveMap_ReturnsEmpty()
    {
        var map = MakeMapWithRoot(caseSensitive: false);
        map.Add("\\A.txt", MakeFile());

        Assert.Empty(map.FindCaseCollisions(5));
    }

    /// <summary>
    /// The scan stops at the requested number of examples, and zero asks for none.
    /// </summary>
    [Fact]
    public void FindCaseCollisions_MoreCollisionsThanRequested_StopsAtTheLimit()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        foreach (var name in new[] { "a", "A", "b", "B", "c", "C" })
        {
            map.Add($"\\{name}", MakeFile());
        }

        Assert.Equal(2, map.FindCaseCollisions(2).Count);
        Assert.Equal(3, map.FindCaseCollisions(10).Count);
        Assert.Empty(map.FindCaseCollisions(0));
    }

    /// <summary>
    /// A colliding directory and its children are all reported, not only the directory pair.
    /// </summary>
    [Fact]
    public void FindCaseCollisions_CollidingDirectoriesWithChildren_ReportsTheChildrenToo()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        map.Add("\\Dir", MakeDir());
        map.Add("\\dir", MakeDir());
        map.Add("\\Dir\\x.txt", MakeFile());
        map.Add("\\dir\\x.txt", MakeFile());

        var collisions = map.FindCaseCollisions(10);

        Assert.Equal([("\\Dir", "\\dir"), ("\\Dir\\x.txt", "\\dir\\x.txt")], collisions);
    }

    /// <summary>
    /// The scan agrees with the replacement: it finds a pair exactly when moving the contents into
    /// a case-insensitive map throws.
    /// </summary>
    /// <param name="collide">Whether the map holds a colliding pair.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FindCaseCollisions_AgreesWithReplacementIntoCaseInsensitiveMap(bool collide)
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        map.Add("\\one", MakeFile());
        map.Add(collide ? "\\ONE" : "\\two", MakeFile());

        var found = map.FindCaseCollisions(5).Count > 0;

        Assert.Equal(collide, found);
        var target = new FileNodeMap();
        if (collide)
        {
            Assert.Throws<InvalidDataException>(() => target.ReplaceAll(map.GetAllNodes()));
        }
        else
        {
            target.ReplaceAll(map.GetAllNodes());
            Assert.Equal(map.Count, target.Count);
        }
    }

    /// <summary>
    /// A replacement of a case-insensitive map's contents with colliding names is refused too.
    /// </summary>
    [Fact]
    public void ReplaceAll_CaseInsensitiveMapWithCollidingNames_Throws()
    {
        var map = MakeMapWithRoot(caseSensitive: false);

        Assert.Throws<InvalidDataException>(() => map.ReplaceAll(
        [
            new("\\A.txt", MakeFile()),
            new("\\a.txt", MakeFile()),
        ]));
    }

    /// <summary>
    /// An image holding two names that differ only by case loads with both on a case-sensitive disk.
    /// </summary>
    [Fact]
    public void Load_ImageWithCaseVariantNamesCaseSensitive_KeepsBoth()
    {
        var path = SaveImageWithCaseVariants();

        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _, caseSensitive: true);

        Assert.True(loaded.TryGet("\\A.txt", out var upper));
        Assert.True(loaded.TryGet("\\a.txt", out var lower));
        Assert.Equal("upper"u8.ToArray(), upper!.FileData!.ToArray((long)upper.FileInfo.FileSize));
        Assert.Equal("lower"u8.ToArray(), lower!.FileData!.ToArray((long)lower.FileInfo.FileSize));
    }

    /// <summary>
    /// The same image is refused, not silently reduced to one file, on a case-insensitive disk.
    /// </summary>
    [Fact]
    public void Load_ImageWithCaseVariantNamesCaseInsensitive_Throws()
    {
        var path = SaveImageWithCaseVariants();

        Assert.Throws<InvalidDataException>(() => DiskImageSerializer.Load(path, out _, out _, null, out _));
    }

    /// <summary>
    /// An ordinary image loads into a case-insensitive map by default, so lookups ignore case.
    /// </summary>
    [Fact]
    public void Load_ImageWithoutCollisionsDefaultMode_LooksUpIgnoringCase()
    {
        var map = MakeMapWithRoot(caseSensitive: false);
        map.Add("\\Folder", MakeDir());
        map.Add("\\Folder\\Readme.TXT", MakeFile("hi"u8));
        var path = Path.Combine(_dir, $"{Guid.NewGuid()}.mdr");
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, 0);

        var loaded = DiskImageSerializer.Load(path, out _, out _, null, out _);

        Assert.False(loaded.CaseSensitive);
        Assert.True(loaded.TryGet("\\folder\\readme.txt", out _));
    }

    /// <summary>
    /// A case-sensitive memory file system keeps names that differ only by case apart.
    /// </summary>
    [Fact]
    public void MemoryFileSystem_CaseSensitiveFlag_BuildsCaseSensitiveNodeMap()
    {
        var fs = new MemoryFileSystem(Capacity, "L", caseSensitive: true);

        Assert.True(fs.NodeMap.CaseSensitive);
    }

    /// <summary>
    /// The default memory file system stays case-insensitive.
    /// </summary>
    [Fact]
    public void MemoryFileSystem_Default_BuildsCaseInsensitiveNodeMap()
    {
        var fs = new MemoryFileSystem(Capacity, "L");

        Assert.False(fs.NodeMap.CaseSensitive);
    }

    /// <summary>
    /// Exact matching compares characters as written.
    /// </summary>
    /// <param name="pattern">The glob pattern.</param>
    /// <param name="name">The candidate name.</param>
    /// <param name="expected">Whether the name matches.</param>
    [Theory]
    [InlineData("foo.txt", "foo.txt", true)]
    [InlineData("FOO.TXT", "foo.txt", false)]
    [InlineData("F*.txt", "foo.txt", false)]
    [InlineData("f*.txt", "foo.txt", true)]
    [InlineData("F?o.txt", "foo.txt", false)]
    public void Match_IgnoreCaseFalse_ComparesExactly(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.Match(pattern, name, ignoreCase: false));
    }

    /// <summary>
    /// <see cref="WildcardMatcher.Matches"/> passes the case flag on.
    /// </summary>
    [Fact]
    public void Matches_IgnoreCaseFalse_ComparesExactly()
    {
        Assert.False(WildcardMatcher.Matches("FOO*", "foo.txt", ignoreCase: false));
        Assert.True(WildcardMatcher.Matches("FOO*", "foo.txt"));
    }

    /// <summary>
    /// A wildcard directory listing on a case-sensitive map only returns the exact-case match.
    /// </summary>
    [Fact]
    public void Build_CaseSensitiveMapWithPattern_FiltersByExactCase()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        map.Add("\\Readme.md", MakeFile());
        map.Add("\\readme.txt", MakeFile());

        Assert.Empty(Enumerate(map, "\\", "READ*"));
        Assert.Equal(["Readme.md"], Enumerate(map, "\\", "Read*"));
    }

    /// <summary>
    /// Lists the names <see cref="DirectoryEnumeration"/> returns for a directory.
    /// </summary>
    /// <param name="map">The node map.</param>
    /// <param name="dirPath">The directory to list.</param>
    /// <param name="pattern">The glob filter.</param>
    /// <returns>The entry names in order.</returns>
    private static List<string> Enumerate(FileNodeMap map, string dirPath, string pattern)
    {
        map.TryGet(dirPath, out var dir);
        var context = DirectoryEnumeration.Build(map, dir!, pattern, null);

        var names = new List<string>();
        while (context.TryNext(out var name, out _))
        {
            names.Add(name!);
        }

        return names;
    }

    /// <summary>
    /// Builds a map holding just the root directory.
    /// </summary>
    /// <param name="caseSensitive">Whether the map compares names case-sensitively.</param>
    /// <returns>The map.</returns>
    private static FileNodeMap MakeMapWithRoot(bool caseSensitive)
    {
        var map = new FileNodeMap(caseSensitive);
        map.Add("\\", new() { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } });
        return map;
    }

    /// <summary>
    /// Saves an image that holds <c>\A.txt</c> and <c>\a.txt</c> with different content.
    /// </summary>
    /// <returns>The image path.</returns>
    private string SaveImageWithCaseVariants()
    {
        var map = MakeMapWithRoot(caseSensitive: true);
        map.Add("\\A.txt", MakeFile("upper"u8));
        map.Add("\\a.txt", MakeFile("lower"u8));

        var path = Path.Combine(_dir, $"{Guid.NewGuid()}.mdr");
        DiskImageSerializer.SaveIncremental(map, Capacity, "Label", path, 0);
        return path;
    }

    /// <summary>
    /// Creates a directory node.
    /// </summary>
    /// <returns>The node.</returns>
    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    /// <summary>
    /// Creates an empty file node with the given allocation.
    /// </summary>
    /// <param name="allocation">The allocated size in bytes.</param>
    /// <returns>The node.</returns>
    private static FileNode MakeFile(ulong allocation = 0) => new()
    {
        FileInfo =
        {
            FileAttributes = (uint)FileAttributes.Normal,
            AllocationSize = FileNode.AlignToAllocationUnit(allocation),
            IndexNumber = FileNode.NewIndexNumber(),
        },
    };

    /// <summary>
    /// A directory created while a scope lists <c>Cache</c> is the app's own only when the name
    /// matches the way the disk compares names: <c>cache</c> on a case-sensitive disk is a user change.
    /// </summary>
    [Fact]
    public void Create_CaseSensitiveDisk_DirectoryDifferingByCaseFromAppFolder_CountsAsUserWrite()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label", caseSensitive: true);
        using var scope = fs.BeginAppFolders(["Cache"]);

        fs.Create("\\cache", 0, 0, (uint)FileAttributes.Directory, [], 0, out _, out _, out _, out _);

        Assert.NotNull(fs.LastContentWriteTimeUtc);
    }

    /// <summary>
    /// On a case-insensitive disk the same name is the app's folder, so it is not a user write.
    /// </summary>
    [Fact]
    public void Create_CaseInsensitiveDisk_DirectoryDifferingByCaseFromAppFolder_IsNotUserWrite()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        using var scope = fs.BeginAppFolders(["Cache"]);

        fs.Create("\\cache", 0, 0, (uint)FileAttributes.Directory, [], 0, out _, out _, out _, out _);

        Assert.Null(fs.LastContentWriteTimeUtc);
    }

    /// <summary>
    /// A file system built on an existing case-sensitive map takes the map's mode for its app folders.
    /// </summary>
    [Fact]
    public void BeginAppFolders_FileSystemOverExistingCaseSensitiveMap_FollowsTheMapsMode()
    {
        var map = new FileNodeMap(caseSensitive: true);
        var fs = new MemoryFileSystem(1024 * 1024, "Label", map);
        using var scope = fs.BeginAppFolders(["Cache"]);

        fs.Create("\\cache", 0, 0, (uint)FileAttributes.Directory, [], 0, out _, out _, out _, out _);

        Assert.NotNull(fs.LastContentWriteTimeUtc);
    }

    /// <summary>
    /// Creates a file node holding <paramref name="content"/>.
    /// </summary>
    /// <param name="content">The file's bytes.</param>
    /// <returns>The node.</returns>
    private static FileNode MakeFile(ReadOnlySpan<byte> content) => new()
    {
        FileInfo =
        {
            FileAttributes = (uint)FileAttributes.Normal,
            AllocationSize = FileNode.AlignToAllocationUnit((ulong)content.Length),
            FileSize = (ulong)content.Length,
            IndexNumber = FileNode.NewIndexNumber(),
        },
        FileData = FileContent.FromSpan(content, FileNode.AlignToAllocationUnit((ulong)content.Length)),
    };
}
