using Fsp;

namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemStreamTests
{
    private const uint DirectoryFileOption = 0x1;

    [Fact]
    public void Create_StreamOnExistingFile_StoresANodeUnderTheStreamPath()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");

        var stream = CreateFile(fs, "\\a.txt:s");

        Assert.True(fs.NodeMap.TryGet("\\a.txt:s", out var stored));
        Assert.Same(stream, stored);
        Assert.True(stream.IsStream);
        Assert.False(stream.IsDirectory);
    }

    [Fact]
    public void Create_StreamOnMissingFile_CreatesTheEmptyFileToo()
    {
        var fs = NewFileSystem();

        CreateFile(fs, "\\a.txt:s");

        Assert.True(fs.NodeMap.TryGet("\\a.txt", out var owner));
        Assert.False(owner!.IsDirectory);
        Assert.Equal(0UL, owner.FileInfo.FileSize);
        Assert.True(fs.NodeMap.TryGet("\\a.txt:s", out _));
    }

    [Fact]
    public void Create_StreamNameWithDataSuffix_StoresUnderTheNormalizedKey()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");

        CreateFile(fs, "\\a.txt:s:$DATA");

        Assert.True(fs.NodeMap.TryGet("\\a.txt:s", out _));
        Assert.False(fs.NodeMap.TryGet("\\a.txt:s:$DATA", out _));
    }

    [Fact]
    public void Create_DefaultStreamName_CollidesWithTheExistingFile()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");

        var status = Create(fs, "\\a.txt::$DATA", FileAttributes.Normal, 0, out _);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, status);
    }

    [Fact]
    public void Create_StreamAlreadyExists_ReturnsCollision()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt:s");

        var status = Create(fs, "\\a.txt:S", FileAttributes.Normal, 0, out _);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_COLLISION, status);
    }

    [Fact]
    public void Create_StreamOnDirectory_Succeeds()
    {
        var fs = NewFileSystem();
        CreateDirectory(fs, "\\dir");

        CreateFile(fs, "\\dir:note");

        Assert.True(fs.NodeMap.TryGet("\\dir:note", out var stream));
        Assert.False(stream!.IsDirectory);
    }

    [Fact]
    public void Create_StreamAsDirectory_ReturnsNameInvalid()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");

        var viaAttribute = Create(fs, "\\a.txt:s", FileAttributes.Directory, 0, out _);
        var viaOption = fs.Create("\\a.txt:t", DirectoryFileOption, 0, (uint)FileAttributes.Normal, [], 0, out _, out _, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_INVALID, viaAttribute);
        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_INVALID, viaOption);
        Assert.False(fs.NodeMap.TryGet("\\a.txt:s", out _));
    }

    [Fact]
    public void Create_StreamThatDoesNotFit_DoesNotLeaveTheImplicitFileBehind()
    {
        var fs = NewFileSystem(capacity: 4096);

        var status = Create(fs, "\\new.txt:big", FileAttributes.Normal, 8192, out _);

        Assert.Equal(FileSystemBase.STATUS_DISK_FULL, status);
        Assert.False(fs.NodeMap.TryGet("\\new.txt", out _));
        Assert.False(fs.NodeMap.TryGet("\\new.txt:big", out _));
    }

    [Fact]
    public void Create_StreamThatDoesNotFitOnAnExistingFile_KeepsTheFile()
    {
        var fs = NewFileSystem(capacity: 4096);
        CreateFile(fs, "\\a.txt");

        var status = Create(fs, "\\a.txt:big", FileAttributes.Normal, 8192, out _);

        Assert.Equal(FileSystemBase.STATUS_DISK_FULL, status);
        Assert.True(fs.NodeMap.TryGet("\\a.txt", out _));
    }

    [Fact]
    public void Create_StreamWhoseParentDirectoryIsMissing_ReturnsPathNotFound()
    {
        var fs = NewFileSystem();

        var status = Create(fs, "\\nodir\\a.txt:s", FileAttributes.Normal, 0, out _);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_PATH_NOT_FOUND, status);
        Assert.False(fs.NodeMap.TryGet("\\nodir\\a.txt", out _));
    }

    [Fact]
    public void GetDirInfoByName_NameOfAStream_ReturnsNameNotFound()
    {
        var fs = NewFileSystem();
        fs.Init(null!);
        CreateFile(fs, "\\a.txt:s");
        fs.Open("\\", 0, 0, out var root, out _, out _, out _);

        var stream = fs.GetDirInfoByName(root!, null!, "a.txt:s", out _, out _);
        var file = fs.GetDirInfoByName(root!, null!, "a.txt", out _, out _);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, stream);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, file);
    }

    [Theory]
    [InlineData("\\a.txt:")]
    [InlineData("\\a.txt:s:$INDEX_ALLOCATION")]
    [InlineData("\\a.txt:s*")]
    [InlineData("\\a:b\\c.txt")]
    public void Create_MalformedName_ReturnsNameInvalid(string name)
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");

        var status = Create(fs, name, FileAttributes.Normal, 0, out _);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_INVALID, status);
    }

    [Fact]
    public void Create_StreamWithAllocation_CountsAgainstTheCapacity()
    {
        var fs = NewFileSystem(capacity: 8192);
        CreateFile(fs, "\\a.txt");

        var fits = Create(fs, "\\a.txt:small", FileAttributes.Normal, 4096, out _);
        var tooBig = Create(fs, "\\a.txt:big", FileAttributes.Normal, 8192, out _);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fits);
        Assert.Equal(FileSystemBase.STATUS_DISK_FULL, tooBig);
        Assert.Equal(4096UL, fs.NodeMap.GetTotalAllocated());
    }

    [Fact]
    public void Open_StreamWithDataSuffix_FindsTheStream()
    {
        var fs = NewFileSystem();
        var stream = CreateFile(fs, "\\a.txt:s");

        var status = fs.Open("\\a.txt:s:$DATA", 0, 0, out var node, out _, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Same(stream, node);
    }

    [Fact]
    public void Open_DefaultStreamName_OpensTheFileItself()
    {
        var fs = NewFileSystem();
        var file = CreateFile(fs, "\\a.txt");

        var status = fs.Open("\\a.txt::$DATA", 0, 0, out var node, out _, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Same(file, node);
    }

    [Fact]
    public void Open_MissingStream_ReturnsNameNotFound()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");

        var status = fs.Open("\\a.txt:nope", 0, 0, out _, out _, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, status);
    }

    [Fact]
    public void Open_MalformedStreamName_ReturnsNameInvalid()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");

        var status = fs.Open("\\a.txt:s:$INDEX_ALLOCATION", 0, 0, out _, out _, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_INVALID, status);
    }

    [Fact]
    public void GetSecurityByName_Stream_ReportsTheStreamsAttributes()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt:s");
        byte[] security = [];

        var status = fs.GetSecurityByName("\\a.txt:s:$DATA", out var attributes, ref security);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Equal(0U, attributes & (uint)FileAttributes.Directory);
    }

    [Fact]
    public void GetSecurityByName_MalformedStreamName_ReturnsNameInvalid()
    {
        var fs = NewFileSystem();
        byte[] security = [];

        var status = fs.GetSecurityByName("\\a.txt:", out _, ref security);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_INVALID, status);
    }

    [Fact]
    public void GetSecurity_Stream_ReturnsTheSecurityOfItsFile()
    {
        var fs = NewFileSystem();
        var file = CreateFile(fs, "\\a.txt");
        var stream = CreateFile(fs, "\\a.txt:s");
        var descriptor = new System.Security.AccessControl.RawSecurityDescriptor("O:BAG:BAD:P(A;;FR;;;WD)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        file.FileSecurity = bytes;
        byte[] result = [];

        var status = fs.GetSecurity(stream, null!, ref result);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public void GetStreamEntry_FileWithStreams_ReportsTheDefaultStreamThenTheNamedOnes()
    {
        var fs = NewFileSystem();
        var file = CreateFile(fs, "\\a.txt");
        fs.SetFileSize(file, null!, 100, setAllocationSize: false, out _);
        var zone = CreateFile(fs, "\\a.txt:Zone.Identifier");
        fs.SetFileSize(zone, null!, 25, setAllocationSize: false, out _);
        CreateFile(fs, "\\a.txt:another");
        CreateFile(fs, "\\b.txt:unrelated");

        var entries = EnumerateStreams(fs, file);

        Assert.Equal(["", "another", "Zone.Identifier"], entries.Select(e => e.Name));
        Assert.Equal(100UL, entries[0].Size);
        Assert.Equal(25UL, entries[2].Size);
    }

    [Fact]
    public void GetStreamEntry_FileWithoutStreams_ReportsOnlyTheDefaultStream()
    {
        var fs = NewFileSystem();
        var file = CreateFile(fs, "\\a.txt");

        var entries = EnumerateStreams(fs, file);

        Assert.Equal([""], entries.Select(e => e.Name));
    }

    [Fact]
    public void GetStreamEntry_Directory_ReportsOnlyNamedStreams()
    {
        var fs = NewFileSystem();
        var directory = CreateDirectory(fs, "\\dir");
        CreateFile(fs, "\\dir:note");

        var entries = EnumerateStreams(fs, directory);

        Assert.Equal(["note"], entries.Select(e => e.Name));
    }

    [Fact]
    public void GetStreamEntry_HandleToAStream_ReportsTheStreamsOfItsFile()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");
        var first = CreateFile(fs, "\\a.txt:one");
        CreateFile(fs, "\\a.txt:two");

        var entries = EnumerateStreams(fs, first);

        Assert.Equal(["", "one", "two"], entries.Select(e => e.Name));
    }

    [Fact]
    public void ReadDirectoryEntry_FileWithStreams_ListsOnlyTheFile()
    {
        var fs = NewFileSystem();
        fs.Init(null!);
        CreateFile(fs, "\\a.txt:s");
        fs.Open("\\", 0, 0, out var root, out _, out _, out _);
        var names = new List<string>();
        object? context = null;

        while (fs.ReadDirectoryEntry(root!, null!, null, null, ref context, out var name, out _))
        {
            names.Add(name!);
        }

        Assert.Equal(["a.txt"], names);
    }

    [Fact]
    public void Rename_FileWithStreams_MovesTheStreamsAlong()
    {
        var fs = NewFileSystem();
        var file = CreateFile(fs, "\\a.txt");
        var stream = CreateFile(fs, "\\a.txt:s");

        var status = fs.Rename(file, null!, "\\a.txt", "\\b.txt", replaceIfExists: false);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.False(fs.NodeMap.TryGet("\\a.txt:s", out _));
        Assert.True(fs.NodeMap.TryGet("\\b.txt:s", out var moved));
        Assert.Same(stream, moved);
        Assert.Equal("\\b.txt:s", stream.FilePath);
        Assert.Equal("b.txt:s", stream.LeafName);
    }

    [Fact]
    public void Rename_DirectoryWithFilesWithStreams_MovesEveryStream()
    {
        var fs = NewFileSystem();
        var directory = CreateDirectory(fs, "\\dir");
        CreateFile(fs, "\\dir:note");
        CreateFile(fs, "\\dir\\a.txt:s");

        var status = fs.Rename(directory, null!, "\\dir", "\\moved", replaceIfExists: false);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.True(fs.NodeMap.TryGet("\\moved:note", out _));
        Assert.True(fs.NodeMap.TryGet("\\moved\\a.txt:s", out _));
        Assert.False(fs.NodeMap.TryGet("\\dir:note", out _));
        Assert.False(fs.NodeMap.TryGet("\\dir\\a.txt:s", out _));
    }

    [Fact]
    public void Rename_OnlyTheCaseOfAFileWithStreams_KeepsTheStreams()
    {
        var fs = NewFileSystem();
        var file = CreateFile(fs, "\\a.txt");
        CreateFile(fs, "\\a.txt:s");

        var status = fs.Rename(file, null!, "\\a.txt", "\\A.TXT", replaceIfExists: false);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.True(fs.NodeMap.TryGet("\\A.TXT:s", out var stream));
        Assert.Equal("\\A.TXT:s", stream!.FilePath);
    }

    [Fact]
    public void Rename_OverAnExistingFile_ReplacesItsStreamsToo()
    {
        var fs = NewFileSystem();
        var source = CreateFile(fs, "\\a.txt");
        CreateFile(fs, "\\b.txt");
        CreateFile(fs, "\\b.txt:old");

        var status = fs.Rename(source, null!, "\\a.txt", "\\b.txt", replaceIfExists: true);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.False(fs.NodeMap.TryGet("\\b.txt:old", out _));
        Assert.Equal(0UL, fs.NodeMap.GetTotalAllocated());
    }

    [Fact]
    public void Rename_StreamWithinItsFile_Succeeds()
    {
        var fs = NewFileSystem();
        var stream = CreateFile(fs, "\\a.txt:one");

        var status = fs.Rename(stream, null!, "\\a.txt:one", "\\a.txt:two", replaceIfExists: false);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.True(fs.NodeMap.TryGet("\\a.txt:two", out _));
        Assert.False(fs.NodeMap.TryGet("\\a.txt:one", out _));
    }

    [Fact]
    public void Rename_StreamToAnotherFile_ReturnsNameInvalid()
    {
        var fs = NewFileSystem();
        var stream = CreateFile(fs, "\\a.txt:one");
        CreateFile(fs, "\\b.txt");

        var status = fs.Rename(stream, null!, "\\a.txt:one", "\\b.txt:one", replaceIfExists: false);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_INVALID, status);
        Assert.True(fs.NodeMap.TryGet("\\a.txt:one", out _));
    }

    [Fact]
    public void Rename_FileToAStreamName_ReturnsNameInvalid()
    {
        var fs = NewFileSystem();
        var file = CreateFile(fs, "\\a.txt");
        CreateFile(fs, "\\b.txt");

        var status = fs.Rename(file, null!, "\\a.txt", "\\b.txt:s", replaceIfExists: false);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_INVALID, status);
        Assert.True(fs.NodeMap.TryGet("\\a.txt", out _));
    }

    [Fact]
    public void Cleanup_DeletingAFile_DeletesItsStreams()
    {
        var fs = NewFileSystem();
        var file = CreateFile(fs, "\\a.txt");
        Create(fs, "\\a.txt:s", FileAttributes.Normal, 4096, out _);

        fs.Cleanup(file, null!, "\\a.txt", FileSystemBase.CleanupDelete);

        Assert.False(fs.NodeMap.TryGet("\\a.txt", out _));
        Assert.False(fs.NodeMap.TryGet("\\a.txt:s", out _));
        Assert.Equal(0UL, fs.NodeMap.GetTotalAllocated());
    }

    [Fact]
    public void Cleanup_DeletingAStream_LeavesTheFile()
    {
        var fs = NewFileSystem();
        CreateFile(fs, "\\a.txt");
        var stream = CreateFile(fs, "\\a.txt:s");

        fs.Cleanup(stream, null!, "\\a.txt:s:$DATA", FileSystemBase.CleanupDelete);

        Assert.True(fs.NodeMap.TryGet("\\a.txt", out _));
        Assert.False(fs.NodeMap.TryGet("\\a.txt:s", out _));
    }

    [Fact]
    public void CanDelete_DirectoryHoldingOnlyItsOwnStreams_Succeeds()
    {
        var fs = NewFileSystem();
        var directory = CreateDirectory(fs, "\\dir");
        CreateFile(fs, "\\dir:note");

        var status = fs.CanDelete(directory, null!, "\\dir");
        fs.Cleanup(directory, null!, "\\dir", FileSystemBase.CleanupDelete);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.False(fs.NodeMap.TryGet("\\dir", out _));
        Assert.False(fs.NodeMap.TryGet("\\dir:note", out _));
    }

    [Fact]
    public void SetReparsePoint_OnAStream_ReturnsInvalidParameter()
    {
        var fs = NewFileSystem();
        var stream = CreateFile(fs, "\\a.txt:s");
        byte[] data = [0x0C, 0x00, 0x00, 0xA0, 0x00, 0x00, 0x00, 0x00];

        var status = fs.SetReparsePoint(stream, null!, "\\a.txt:s", data);

        Assert.Equal(FileSystemBase.STATUS_INVALID_PARAMETER, status);
        Assert.Null(stream.ReparseData);
    }

    [Fact]
    public void TryReplaceContents_SourceWithStreams_CopiesThemIndependently()
    {
        var source = NewFileSystem();
        CreateFile(source, "\\a.txt");
        var stream = CreateFile(source, "\\a.txt:s");
        var target = NewFileSystem();

        var replaced = target.TryReplaceContents(source.NodeMap, out _);

        Assert.True(replaced);
        Assert.True(target.NodeMap.TryGet("\\a.txt:s", out var copy));
        Assert.NotSame(stream, copy);
        Assert.True(copy!.IsStream);
    }

    private static MemoryFileSystem NewFileSystem(ulong capacity = 1024 * 1024) => new(capacity, "Label");

    private static int Create(MemoryFileSystem fs, string path, FileAttributes attributes, ulong allocationSize, out FileNode? node)
    {
        var status = fs.Create(path, 0, 0, (uint)attributes, [], allocationSize, out var created, out _, out _, out _);
        node = (FileNode?)created;
        return status;
    }

    private static FileNode CreateFile(MemoryFileSystem fs, string path)
    {
        var status = Create(fs, path, FileAttributes.Normal, 0, out var node);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        return node!;
    }

    private static FileNode CreateDirectory(MemoryFileSystem fs, string path)
    {
        var status = Create(fs, path, FileAttributes.Directory, 0, out var node);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        return node!;
    }

    private static List<(string Name, ulong Size)> EnumerateStreams(MemoryFileSystem fs, FileNode node)
    {
        List<(string Name, ulong Size)> entries = [];
        object? context = null;
        while (fs.GetStreamEntry(node, null!, ref context, out var name, out var size, out _))
        {
            entries.Add((name!, size));
        }

        return entries;
    }
}
