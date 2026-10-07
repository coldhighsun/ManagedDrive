namespace ManagedDrive.Tests;

public sealed class SnapshotExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    public SnapshotExtractorTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void Extract_File_WritesItsContentToTheOutputPath()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "copy.txt");

        var result = SnapshotExtractor.Extract(map, "\\Folder\\a.txt", output, overwrite: false);

        Assert.Equal("alpha"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Equal(new SnapshotExtractResult(1, 0, 5, 0), result);
    }

    [Fact]
    public void Extract_FileIntoAnExistingDirectory_KeepsItsName()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "target");
        Directory.CreateDirectory(output);

        SnapshotExtractor.Extract(map, "\\Folder\\a.txt", output, overwrite: false);

        Assert.Equal("alpha"u8.ToArray(), File.ReadAllBytes(Path.Combine(output, "a.txt")));
    }

    [Fact]
    public void Extract_FileToMissingPathWithTrailingSeparator_CreatesThatDirectory()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "newdir") + Path.DirectorySeparatorChar;

        SnapshotExtractor.Extract(map, "\\Folder\\a.txt", output, overwrite: false);

        Assert.Equal("alpha"u8.ToArray(), File.ReadAllBytes(Path.Combine(_dir, "newdir", "a.txt")));
    }

    [Fact]
    public void Extract_FileWhoseOutputParentIsMissing_CreatesIt()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "deep", "er", "a.txt");

        SnapshotExtractor.Extract(map, "\\Folder\\a.txt", output, overwrite: false);

        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Extract_EmptyFile_CreatesAnEmptyFile()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "empty.txt");

        var result = SnapshotExtractor.Extract(map, "\\empty.txt", output, overwrite: false);

        Assert.Empty(File.ReadAllBytes(output));
        Assert.Equal(1, result.Files);
    }

    [Fact]
    public void Extract_Directory_CopiesTheTreeIncludingEmptyDirectories()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "out");

        var result = SnapshotExtractor.Extract(map, "\\Folder", output, overwrite: false);

        Assert.Equal("alpha"u8.ToArray(), File.ReadAllBytes(Path.Combine(output, "a.txt")));
        Assert.Equal("beta"u8.ToArray(), File.ReadAllBytes(Path.Combine(output, "Sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(output, "EmptyDir")));
        Assert.Equal(2, result.Files);
        Assert.Equal(9UL, result.Bytes);
        Assert.Equal(3, result.Directories);
    }

    [Fact]
    public void Extract_Root_CopiesTheWholeDisk()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "all");

        var result = SnapshotExtractor.Extract(map, "\\", output, overwrite: false);

        Assert.True(File.Exists(Path.Combine(output, "empty.txt")));
        Assert.True(File.Exists(Path.Combine(output, "Folder", "Sub", "b.txt")));
        Assert.Equal(3, result.Files);
    }

    [Theory]
    [InlineData("Folder/a.txt")]
    [InlineData("\\Folder\\a.txt")]
    [InlineData("/Folder/a.txt/")]
    [InlineData("  \\folder\\A.TXT  ")]
    public void Extract_SourceTypedInDifferentForms_FindsTheFile(string typed)
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, $"{Guid.NewGuid()}.txt");

        SnapshotExtractor.Extract(map, typed, output, overwrite: false);

        Assert.Equal("alpha"u8.ToArray(), File.ReadAllBytes(output));
    }

    [Fact]
    public void Extract_StreamsAndLinks_AreLeftOut()
    {
        var map = BuildMap();
        map.Add("\\Folder\\a.txt:Zone.Identifier", MakeFile("[ZoneTransfer]"u8));
        var link = MakeFile([]);
        link.ApplyReparseData(SymlinkBuffer);
        map.Add("\\Folder\\link", link);
        var output = Path.Combine(_dir, "out");

        var result = SnapshotExtractor.Extract(map, "\\Folder", output, overwrite: false);

        Assert.False(File.Exists(Path.Combine(output, "link")));
        Assert.Equal(["a.txt", "EmptyDir", "Sub"], Directory.EnumerateFileSystemEntries(output).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, result.SkippedLinks);
        Assert.Equal(2, result.Files);
    }

    [Fact]
    public void Extract_ExistingFileWithoutOverwrite_ThrowsAndWritesNothing()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(output, "Sub"));
        File.WriteAllText(Path.Combine(output, "Sub", "b.txt"), "keep me");

        var ex = Assert.Throws<IOException>(() => SnapshotExtractor.Extract(map, "\\Folder", output, overwrite: false));

        Assert.Contains("--force", ex.Message);
        Assert.False(File.Exists(Path.Combine(output, "a.txt")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(output, "Sub", "b.txt")));
    }

    [Fact]
    public void Extract_ExistingFileWithOverwrite_ReplacesIt()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "a.txt");
        File.WriteAllText(output, "a much longer previous content");

        SnapshotExtractor.Extract(map, "\\Folder\\a.txt", output, overwrite: true);

        Assert.Equal("alpha"u8.ToArray(), File.ReadAllBytes(output));
    }

    [Fact]
    public void Extract_DirectoryOntoAnExistingFile_Throws()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "out");
        File.WriteAllText(output, "a file");

        Assert.Throws<IOException>(() => SnapshotExtractor.Extract(map, "\\Folder", output, overwrite: true));
    }

    [Fact]
    public void Extract_FileOntoAnExistingDirectoryNamedLikeIt_Throws()
    {
        var map = BuildMap();
        var output = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(output, "a.txt"));

        Assert.Throws<IOException>(() => SnapshotExtractor.Extract(map, "\\Folder\\a.txt", output, overwrite: true));
    }

    [Fact]
    public void Extract_MissingSource_ThrowsFileNotFound()
    {
        var map = BuildMap();

        var ex = Assert.Throws<FileNotFoundException>(() =>
            SnapshotExtractor.Extract(map, "\\nope.txt", Path.Combine(_dir, "x"), overwrite: false));

        Assert.Contains("nope.txt", ex.Message);
    }

    [Fact]
    public void Extract_StreamAsTheSource_ThrowsFileNotFound()
    {
        var map = BuildMap();
        map.Add("\\empty.txt:s", MakeFile("x"u8));

        Assert.Throws<FileNotFoundException>(() =>
            SnapshotExtractor.Extract(map, "\\empty.txt:s", Path.Combine(_dir, "x"), overwrite: false));
    }

    [Fact]
    public void Extract_LinkAsTheSource_ThrowsFileNotFound()
    {
        var map = BuildMap();
        var link = MakeFile([]);
        link.ApplyReparseData(SymlinkBuffer);
        map.Add("\\link", link);

        Assert.Throws<FileNotFoundException>(() =>
            SnapshotExtractor.Extract(map, "\\link", Path.Combine(_dir, "x"), overwrite: false));
    }

    [Fact]
    public void Extract_NodeNamedLikeAParentReference_IsRefusedInsteadOfEscapingTheOutput()
    {
        var map = BuildMap();
        map.Add("\\Folder\\..", MakeFile("evil"u8));
        var output = Path.Combine(_dir, "out");

        var ex = Assert.Throws<IOException>(() => SnapshotExtractor.Extract(map, "\\Folder", output, overwrite: true));

        Assert.Contains("outside", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "evil")));
    }

    [Fact]
    public void Extract_File_KeepsItsLastWriteTime()
    {
        var map = BuildMap();
        var stamp = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Assert.True(map.TryGet("\\Folder\\a.txt", out var node));
        node!.FileInfo.LastWriteTime = (ulong)stamp.ToFileTimeUtc();
        var output = Path.Combine(_dir, "a.txt");

        SnapshotExtractor.Extract(map, "\\Folder\\a.txt", output, overwrite: false);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(output));
    }

    [Theory]
    [InlineData("a", "\\a")]
    [InlineData("\\a\\", "\\a")]
    [InlineData("/a/b", "\\a\\b")]
    [InlineData("\\", "\\")]
    [InlineData("", "\\")]
    public void NormalizeSourcePath_ProducesMapKeys(string typed, string expected)
    {
        Assert.Equal(expected, SnapshotExtractor.NormalizeSourcePath(typed));
    }

    /// <summary>
    /// Header-only REPARSE_DATA_BUFFER: tag IO_REPARSE_TAG_SYMLINK, zero-length payload.
    /// </summary>
    private static readonly byte[] SymlinkBuffer = [0x0C, 0x00, 0x00, 0xA0, 0x00, 0x00, 0x00, 0x00];

    private static FileNodeMap BuildMap()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\empty.txt", MakeFile([]));
        map.Add("\\Folder", MakeDir());
        map.Add("\\Folder\\a.txt", MakeFile("alpha"u8));
        map.Add("\\Folder\\Sub", MakeDir());
        map.Add("\\Folder\\Sub\\b.txt", MakeFile("beta"u8));
        map.Add("\\Folder\\EmptyDir", MakeDir());
        return map;
    }

    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    private static FileNode MakeFile(ReadOnlySpan<byte> content) => new()
    {
        FileInfo =
        {
            FileAttributes = (uint)FileAttributes.Normal,
            AllocationSize = FileNode.AlignToAllocationUnit((ulong)content.Length),
            FileSize = (ulong)content.Length,
            IndexNumber = FileNode.NewIndexNumber(),
        },
        FileData = content.IsEmpty ? null : FileContent.FromSpan(content, FileNode.AlignToAllocationUnit((ulong)content.Length)),
    };
}
