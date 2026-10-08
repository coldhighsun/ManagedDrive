namespace ManagedDrive.Tests;

public sealed class TreemapTests
{
    private static readonly TreemapRect Bounds = new(10, 20, 400, 300);

    [Fact]
    public void Squarify_Empty_ReturnsNothing()
    {
        Assert.Empty(TreemapLayout.Squarify([], Bounds));
    }

    [Fact]
    public void Squarify_SingleItem_FillsTheBounds()
    {
        var rects = TreemapLayout.Squarify([5], Bounds);

        Assert.Equal(Bounds, Assert.Single(rects));
    }

    [Fact]
    public void Squarify_AreasAreProportionalAndCoverTheBounds()
    {
        double[] weights = [60, 25, 10, 3, 2];

        var rects = TreemapLayout.Squarify(weights, Bounds);

        Assert.Equal(Bounds.Area, rects.Sum(rect => rect.Area), 6);
        for (var i = 0; i < weights.Length; i++)
        {
            Assert.Equal(Bounds.Area * weights[i] / weights.Sum(), rects[i].Area, 6);
        }
    }

    [Fact]
    public void Squarify_Rectangles_StayInsideAndDoNotOverlap()
    {
        var weights = Enumerable.Range(1, 40).Select(i => (double)(41 - i) * (41 - i)).ToArray();

        var rects = TreemapLayout.Squarify(weights, Bounds);

        const double Tolerance = 1e-6;
        foreach (var rect in rects)
        {
            Assert.True(rect.X >= Bounds.X - Tolerance && rect.Y >= Bounds.Y - Tolerance);
            Assert.True(rect.Right <= Bounds.Right + Tolerance && rect.Bottom <= Bounds.Bottom + Tolerance);
        }

        for (var i = 0; i < rects.Length; i++)
        {
            for (var j = i + 1; j < rects.Length; j++)
            {
                var overlapWidth = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].X, rects[j].X);
                var overlapHeight = Math.Min(rects[i].Bottom, rects[j].Bottom) - Math.Max(rects[i].Y, rects[j].Y);
                Assert.False(overlapWidth > Tolerance && overlapHeight > Tolerance, $"{i} overlaps {j}");
            }
        }
    }

    [Fact]
    public void Squarify_EqualWeights_GivesNearlySquareCells()
    {
        var rects = TreemapLayout.Squarify(Enumerable.Repeat(1.0, 12).ToArray(), new(0, 0, 400, 300));

        Assert.All(rects, rect => Assert.InRange(Math.Max(rect.Width, rect.Height) / Math.Min(rect.Width, rect.Height), 1.0, 2.0));
    }

    [Fact]
    public void Squarify_ZeroAndNegativeWeights_GetEmptyRectangles()
    {
        var rects = TreemapLayout.Squarify([3, 0, -1, 1], Bounds);

        Assert.Equal(0, rects[1].Area);
        Assert.Equal(0, rects[2].Area);
        Assert.Equal(Bounds.Area, rects[0].Area + rects[3].Area, 6);
    }

    [Fact]
    public void Squarify_EmptyBounds_ReturnsEmptyRectangles()
    {
        var rects = TreemapLayout.Squarify([1, 2], new(0, 0, 0, 100));

        Assert.All(rects, rect => Assert.Equal(0, rect.Area));
    }

    [Fact]
    public void Squarify_ExtremelyUnevenWeights_StillCoverTheBounds()
    {
        var rects = TreemapLayout.Squarify([1_000_000_000, 1, 1, 1], Bounds);

        Assert.Equal(Bounds.Area, rects.Sum(rect => rect.Area), 3);
    }

    [Fact]
    public void Build_DefaultOptions_ShowsOnlyDirectChildren()
    {
        var root = SpaceUsageAnalyzer.BuildTree(BuildMap().GetAllNodes());

        var cells = TreemapBuilder.Build(root, new(0, 0, 800, 600));

        Assert.Contains(cells, cell => cell.Node?.Path == "\\Folder");
        Assert.DoesNotContain(cells, cell => cell.Node?.Path == "\\Folder\\a.txt");
    }

    [Fact]
    public void Build_Directory_CellAreaFollowsTheTotalSizeOfItsContents()
    {
        var root = SpaceUsageAnalyzer.BuildTree(BuildMap().GetAllNodes());

        var cells = TreemapBuilder.Build(root, new(0, 0, 800, 600));

        var folder = Assert.Single(cells, cell => cell.Node?.Path == "\\Folder");
        var file = Assert.Single(cells, cell => cell.Node?.Path == "\\other.bin");
        Assert.Equal(file.Rect.Area, folder.Rect.Area, file.Rect.Area * 0.05);
    }

    [Fact]
    public void Build_DeepTree_ShowsOnlyTheShownDirectorysDirectChildren()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\A", MakeDir());
        map.Add("\\A\\B", MakeDir());
        map.Add("\\A\\B\\deep.bin", MakeFile(400_000));
        map.Add("\\A\\mid.bin", MakeFile(300_000));
        map.Add("\\top.bin", MakeFile(200_000));
        var root = SpaceUsageAnalyzer.BuildTree(map.GetAllNodes());

        var atRoot = TreemapBuilder.Build(root, new(0, 0, 800, 600));
        var inA = TreemapBuilder.Build(root.Children.Single(child => child.Path == "\\A"), new(0, 0, 800, 600));

        Assert.Equal(["\\A", "\\top.bin"], atRoot.Select(cell => cell.Node!.Path).Order());
        Assert.Equal(["\\A\\B", "\\A\\mid.bin"], inA.Select(cell => cell.Node!.Path).Order());
    }

    [Fact]
    public void Build_TinyItems_AreMergedIntoOneBlock()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\big.bin", MakeFile(1_000_000));
        for (var i = 0; i < 20; i++)
        {
            map.Add($"\\tiny{i}.txt", MakeFile(512));
        }

        var cells = TreemapBuilder.Build(SpaceUsageAnalyzer.BuildTree(map.GetAllNodes()), new(0, 0, 200, 200));

        var merged = Assert.Single(cells, cell => cell.Node is null);
        Assert.Equal(20, merged.MergedCount);
        Assert.Equal(20UL * 512, merged.MergedBytes);
        Assert.Equal(2, cells.Count);
    }

    [Fact]
    public void Build_MaxItems_CapsTheCells()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        for (var i = 0; i < 50; i++)
        {
            map.Add($"\\f{i}.bin", MakeFile(100_000));
        }

        var cells = TreemapBuilder.Build(SpaceUsageAnalyzer.BuildTree(map.GetAllNodes()), new(0, 0, 4000, 3000), new(MaxItems: 10));

        Assert.Equal(11, cells.Count);
        Assert.Equal(40, Assert.Single(cells, cell => cell.Node is null).MergedCount);
    }

    [Fact]
    public void Build_EmptyDirectory_HasNoCells()
    {
        var root = SpaceUsageAnalyzer.BuildTree(new FileNodeMap().GetAllNodes());

        Assert.Empty(TreemapBuilder.Build(root, new(0, 0, 100, 100)));
    }

    [Theory]
    [InlineData("a.PNG", FileCategory.Image)]
    [InlineData(".mp4", FileCategory.Video)]
    [InlineData("song.flac", FileCategory.Audio)]
    [InlineData("x.tar", FileCategory.Archive)]
    [InlineData("report.docx", FileCategory.Document)]
    [InlineData("Program.cs", FileCategory.Code)]
    [InlineData("app.EXE", FileCategory.Executable)]
    [InlineData("debug.log", FileCategory.Cache)]
    [InlineData("photo.AVIF", FileCategory.Image)]
    [InlineData("clip.3gp", FileCategory.Video)]
    [InlineData("tune.midi", FileCategory.Audio)]
    [InlineData("disk.vmdk", FileCategory.Archive)]
    [InlineData("backup.tgz", FileCategory.Archive)]
    [InlineData("macro.xlsm", FileCategory.Document)]
    [InlineData("font.woff2", FileCategory.Document)]
    [InlineData("notes.ipynb", FileCategory.Document)]
    [InlineData("Page.razor", FileCategory.Code)]
    [InlineData("Directory.Build.props", FileCategory.Code)]
    [InlineData("query.sql", FileCategory.Code)]
    [InlineData("App.vue", FileCategory.Code)]
    [InlineData("addon.node", FileCategory.Executable)]
    [InlineData("module.wasm", FileCategory.Executable)]
    [InlineData("data.sqlite3", FileCategory.Cache)]
    [InlineData("Main.class", FileCategory.Executable)]
    [InlineData("yarn.lock", FileCategory.Other)]
    [InlineData("graph.dot", FileCategory.Other)]
    [InlineData("google.com", FileCategory.Other)]
    [InlineData("README", FileCategory.Other)]
    [InlineData("weird.zzz", FileCategory.Other)]
    public void FileCategories_Of_MapsExtensions(string name, FileCategory expected)
    {
        Assert.Equal(expected, FileCategories.Of(name));
    }

    [Theory]
    [InlineData("a.png", "AppSpaceImage")]
    [InlineData(".zip", "AppSpaceArchive")]
    [InlineData("(none)", "AppSpaceOther")]
    [InlineData("weird.zzz", "AppSpaceOther")]
    public void SpaceBrushKeys_For_NamesTheThemeBrush(string name, string expected)
    {
        Assert.Equal(expected, SpaceBrushKeys.For(name));
    }

    [Theory]
    [InlineData("AppTheme.Colors.Light.xaml")]
    [InlineData("AppTheme.Colors.Dark.xaml")]
    public void SpaceBrushKeys_EveryKeyHasABrushInTheTheme(string themeFile)
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", themeFile));
        var keys = Enum.GetValues<FileCategory>().Select(SpaceBrushKeys.For).Append(SpaceBrushKeys.Merged).Append(SpaceBrushKeys.Folder);

        foreach (var key in keys)
        {
            Assert.Contains($"x:Key=\"{key}\"", xaml);
        }
    }

    [Theory]
    [InlineData(null, "AppSpaceOther")]
    [InlineData("", "AppSpaceOther")]
    [InlineData("AppSpaceImage", "AppSpaceImage")]
    public void SpaceSwatchConverter_KeyOf_UsesTheOtherKeyWhenThereIsNone(string? key, string expected)
    {
        Assert.Equal(expected, SpaceSwatchConverter.KeyOf([key]));
    }

    [Fact]
    public void SpaceSwatchConverter_KeyOf_NoValues_UsesTheOtherKey()
    {
        Assert.Equal("AppSpaceOther", SpaceSwatchConverter.KeyOf([]));
    }

    [Fact]
    public void SpaceSwatchConverter_Convert_WithoutAnApplication_FallsBackToGrey()
    {
        var converter = new SpaceSwatchConverter();

        var result = converter.Convert(["AppSpaceImage"], typeof(System.Windows.Media.Brush), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Same(System.Windows.Media.Brushes.Gray, result);
    }

    private static FileNodeMap BuildMap()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\Folder", MakeDir());
        map.Add("\\Folder\\a.txt", MakeFile(300_000));
        map.Add("\\Folder\\b.txt", MakeFile(200_000));
        map.Add("\\other.bin", MakeFile(500_000));
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
