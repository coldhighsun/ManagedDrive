namespace ManagedDrive.Tests;

public sealed class SpaceUsageAnalyzerTests
{
    [Fact]
    public void Analyze_EmptyDisk_ReportsZeros()
    {
        var map = new FileNodeMap();

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 5);

        Assert.Equal(0UL, report.TotalAllocated);
        Assert.Equal(0, report.FileCount);
        Assert.Empty(report.TopFiles);
        Assert.Empty(report.TopDirectories);
        Assert.Empty(report.TopExtensions);
    }

    [Fact]
    public void Analyze_TotalsMatchTheMapsAllocatedBytes()
    {
        var map = BuildMap();
        map.Add("\\Folder\\a.txt:Zone.Identifier", MakeFile(40));

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 10);

        Assert.Equal(map.GetTotalAllocated(), report.TotalAllocated);
    }

    [Fact]
    public void Analyze_Counts_SeparateFilesDirectoriesLinksAndStreams()
    {
        var map = BuildMap();
        map.Add("\\Folder\\a.txt:s", MakeFile(10));
        var link = MakeFile(0);
        link.ApplyReparseData(SymlinkBuffer);
        map.Add("\\link", link);

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 10);

        Assert.Equal(4, report.FileCount);
        Assert.Equal(3, report.DirectoryCount);
        Assert.Equal(1, report.LinkCount);
        Assert.Equal(1, report.StreamCount);
    }

    [Fact]
    public void Analyze_Directories_AreSummedRecursively()
    {
        var map = BuildMap();

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 10);

        var folder = Assert.Single(report.TopDirectories, entry => entry.Path == "\\Folder");
        Assert.Equal(512UL + 1024UL, folder.Allocated);
        Assert.Equal(2, folder.FileCount);
        Assert.Equal(["\\Big", "\\Folder", "\\Folder\\Sub"], report.TopDirectories.Select(entry => entry.Path));
    }

    [Fact]
    public void Analyze_TopFiles_AreLargestFirstAndCapped()
    {
        var map = BuildMap();

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 2);

        Assert.Equal(["\\Big\\huge.bin", "\\Folder\\Sub\\b.txt"], report.TopFiles.Select(entry => entry.Path));
    }

    [Fact]
    public void Analyze_EqualSizes_KeepTheEarlierPath()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\a", MakeFile(512));
        map.Add("\\b", MakeFile(512));
        map.Add("\\c", MakeFile(512));

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 2);

        Assert.Equal(["\\a", "\\b"], report.TopFiles.Select(entry => entry.Path));
    }

    [Fact]
    public void Analyze_Extensions_MergeCaseAndNameFilesWithoutOne()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\a.TXT", MakeFile(512));
        map.Add("\\b.txt", MakeFile(1024));
        map.Add("\\README", MakeFile(100));

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 10);

        Assert.Equal(
            [new SpaceUsageExtension(".txt", 2, 1536, 1536), new SpaceUsageExtension(SpaceUsageAnalyzer.NoExtension, 1, 512, 100)],
            report.TopExtensions);
    }

    [Fact]
    public void Analyze_Stream_CountsTowardsItsFile()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\a.txt", MakeFile(512));
        map.Add("\\a.txt:Zone.Identifier", MakeFile(100));

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 10);

        var file = Assert.Single(report.TopFiles);
        Assert.Equal(512UL + 512UL, file.Allocated);
        Assert.Equal(612UL, file.Logical);
        Assert.Equal(1, file.FileCount);
    }

    [Fact]
    public void Analyze_Link_TakesNoSpaceAndIsNotListed()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        var link = MakeFile(0);
        link.ApplyReparseData(SymlinkBuffer);
        map.Add("\\link", link);

        var report = SpaceUsageAnalyzer.Analyze(map.GetAllNodes(), 10);

        Assert.Empty(report.TopFiles);
        Assert.Equal(0, report.FileCount);
        Assert.Equal(1, report.LinkCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SpaceUsageAnalyzer.MaxTop + 1)]
    public void Analyze_TopOutOfRange_Throws(int top)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaceUsageAnalyzer.Analyze(new FileNodeMap().GetAllNodes(), top));
    }

    [Fact]
    public void BuildTree_RootCoversEverything_AndChildrenAreSortedLargestFirst()
    {
        var map = BuildMap();

        var root = SpaceUsageAnalyzer.BuildTree(map.GetAllNodes());

        Assert.Equal(map.GetTotalAllocated(), root.Allocated);
        Assert.Equal(["Big", "Folder", "small.txt"], root.Children.Select(child => child.Name));
        Assert.Same(root, root.Children[0].Parent);
        Assert.Null(root.Parent);
    }

    [Fact]
    public void BuildTree_OrphanStream_IsChargedToTheRoot()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\a.txt", MakeFile(512));
        map.Add("\\a.txt:s", MakeFile(100));
        map.Add("\\gone.txt:s", MakeFile(200));

        var root = SpaceUsageAnalyzer.BuildTree(map.GetAllNodes());

        Assert.Equal(512UL + 512UL + 512UL, root.Allocated);
    }

    /// <summary>
    /// Header-only REPARSE_DATA_BUFFER: tag IO_REPARSE_TAG_SYMLINK, zero-length payload.
    /// </summary>
    private static readonly byte[] SymlinkBuffer = [0x0C, 0x00, 0x00, 0xA0, 0x00, 0x00, 0x00, 0x00];

    [Theory]
    [InlineData(-5, 100)]
    [InlineData(0, 100)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(int.MaxValue, 1000)]
    public void ClampListSize_AnyValue_StaysWithinAllowedRange(int requested, int expected)
    {
        var result = SpaceUsageAnalyzer.ClampListSize(requested);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DefaultListSize_IsInsideAllowedRange()
    {
        Assert.Equal(SpaceUsageAnalyzer.DefaultListSize, SpaceUsageAnalyzer.ClampListSize(SpaceUsageAnalyzer.DefaultListSize));
    }

    private static FileNodeMap BuildMap()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\small.txt", MakeFile(10));
        map.Add("\\Folder", MakeDir());
        map.Add("\\Folder\\a.txt", MakeFile(512));
        map.Add("\\Folder\\Sub", MakeDir());
        map.Add("\\Folder\\Sub\\b.txt", MakeFile(1024));
        map.Add("\\Big", MakeDir());
        map.Add("\\Big\\huge.bin", MakeFile(8192));
        return map;
    }

    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    private static FileNode MakeFile(ulong size) => new()
    {
        FileInfo =
        {
            FileAttributes = (uint)FileAttributes.Normal,
            AllocationSize = FileNode.AlignToAllocationUnit(size),
            FileSize = size,
            IndexNumber = FileNode.NewIndexNumber(),
        },
    };
}
