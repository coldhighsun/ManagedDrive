using System.Buffers.Binary;
using Fsp;

namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemReparsePointTests
{
    private const uint SymlinkTag = 0xA000000C;
    private const uint MountPointTag = 0xA0000003;
    private const uint ReparseAttribute = (uint)FileAttributes.ReparsePoint;

    [Fact]
    public void SetReparsePoint_SymlinkOnEmptyFile_SetsDataTagAndAttribute()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\link");
        var data = MakeReparse(SymlinkTag, 12);

        var status = fs.SetReparsePoint(node, null!, "\\link", data);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Equal(data, node.ReparseData);
        Assert.Equal(SymlinkTag, node.FileInfo.ReparseTag);
        Assert.NotEqual(0U, node.FileInfo.FileAttributes & ReparseAttribute);
    }

    [Fact]
    public void SetReparsePoint_CallerMutatesBufferAfterwards_NodeKeepsItsOwnCopy()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\link");
        var data = MakeReparse(SymlinkTag, 12);
        var expected = data.ToArray();

        fs.SetReparsePoint(node, null!, "\\link", data);
        data[^1] ^= 0xFF;

        Assert.Equal(expected, node.ReparseData);
    }

    [Fact]
    public void SetReparsePoint_JunctionOnEmptyDirectory_Succeeds()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateDirectory(fs, "\\junction");

        var status = fs.SetReparsePoint(node, null!, "\\junction", MakeReparse(MountPointTag, 20));

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Equal(MountPointTag, node.FileInfo.ReparseTag);
        Assert.True(node.IsDirectory);
    }

    [Fact]
    public void SetReparsePoint_JunctionOnFile_ReturnsNotADirectory()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\file");

        var status = fs.SetReparsePoint(node, null!, "\\file", MakeReparse(MountPointTag, 20));

        Assert.Equal(FileSystemBase.STATUS_NOT_A_DIRECTORY, status);
        Assert.Null(node.ReparseData);
    }

    [Fact]
    public void SetReparsePoint_NonEmptyDirectory_ReturnsDirectoryNotEmpty()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateDirectory(fs, "\\dir");
        CreateFile(fs, "\\dir\\child");

        var status = fs.SetReparsePoint(node, null!, "\\dir", MakeReparse(MountPointTag, 20));

        Assert.Equal(FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY, status);
        Assert.Null(node.ReparseData);
    }

    [Fact]
    public void SetReparsePoint_UnsupportedTag_ReturnsTagInvalid()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\file");

        // IO_REPARSE_TAG_LX_SYMLINK (WSL) is a valid Microsoft tag the file system doesn't handle.
        var status = fs.SetReparsePoint(node, null!, "\\file", MakeReparse(0xA000001D, 8));

        Assert.Equal(FileSystemBase.STATUS_IO_REPARSE_TAG_INVALID, status);
        Assert.Null(node.ReparseData);
        Assert.Equal(0U, node.FileInfo.FileAttributes & ReparseAttribute);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void SetReparsePoint_BufferShorterThanHeader_ReturnsDataInvalid(int length)
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\file");

        var status = fs.SetReparsePoint(node, null!, "\\file", new byte[length]);

        Assert.Equal(FileSystemBase.STATUS_IO_REPARSE_DATA_INVALID, status);
    }

    [Fact]
    public void SetReparsePoint_DeclaredLengthDisagreesWithBuffer_ReturnsDataInvalid()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\file");
        var data = MakeReparse(SymlinkTag, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 99);

        var status = fs.SetReparsePoint(node, null!, "\\file", data);

        Assert.Equal(FileSystemBase.STATUS_IO_REPARSE_DATA_INVALID, status);
    }

    [Fact]
    public void SetReparsePoint_BufferLargerThanWindowsLimit_ReturnsDataInvalid()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\file");

        var status = fs.SetReparsePoint(node, null!, "\\file", MakeReparse(SymlinkTag, 16 * 1024));

        Assert.Equal(FileSystemBase.STATUS_IO_REPARSE_DATA_INVALID, status);
    }

    [Fact]
    public void SetReparsePoint_ReplacingWithDifferentTag_ReturnsTagMismatch()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateDirectory(fs, "\\dir");
        fs.SetReparsePoint(node, null!, "\\dir", MakeReparse(MountPointTag, 20));

        var status = fs.SetReparsePoint(node, null!, "\\dir", MakeReparse(SymlinkTag, 20));

        Assert.Equal(FileSystemBase.STATUS_IO_REPARSE_TAG_MISMATCH, status);
        Assert.Equal(MountPointTag, node.FileInfo.ReparseTag);
    }

    [Fact]
    public void SetReparsePoint_ReplacingWithSameTag_ReplacesTarget()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\link");
        fs.SetReparsePoint(node, null!, "\\link", MakeReparse(SymlinkTag, 12));
        var replacement = MakeReparse(SymlinkTag, 30);

        var status = fs.SetReparsePoint(node, null!, "\\link", replacement);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Equal(replacement, node.ReparseData);
    }

    [Theory]
    [InlineData(MountPointTag)]
    [InlineData(SymlinkTag)]
    public void SetReparsePoint_VolumeRoot_ReturnsAccessDeniedAndLeavesRootPlain(uint tag)
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Init(null!);
        Assert.True(fs.NodeMap.TryGet("\\", out var root));

        var status = fs.SetReparsePoint(root!, null!, "\\", MakeReparse(tag, 20));

        Assert.Equal(FileSystemBase.STATUS_ACCESS_DENIED, status);
        Assert.Null(root!.ReparseData);
        Assert.Equal(0U, root.FileInfo.ReparseTag);
        Assert.Equal(0U, root.FileInfo.FileAttributes & ReparseAttribute);
        Assert.False(fs.NodeMap.MayHaveReparsePoints);
    }

    [Fact]
    public void SetReparsePoint_ReadOnlyDisk_ReturnsMediaWriteProtected()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label", readOnly: true);
        var node = new FileNode();

        var status = fs.SetReparsePoint(node, null!, "\\link", MakeReparse(SymlinkTag, 12));

        Assert.Equal(FileSystemBase.STATUS_MEDIA_WRITE_PROTECTED, status);
        Assert.Null(node.ReparseData);
    }

    [Fact]
    public void SetReparsePoint_Succeeds_MarksDiskDirtyAndBumpsMetadataVersion()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\link");
        fs.ClearDirty();
        var versionBefore = node.MetadataVersion;

        fs.SetReparsePoint(node, null!, "\\link", MakeReparse(SymlinkTag, 12));

        Assert.True(fs.IsDirty);
        Assert.True(node.MetadataVersion > versionBefore);
    }

    [Fact]
    public void GetReparsePoint_ReparseNode_ReturnsItsData()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\link");
        var data = MakeReparse(SymlinkTag, 12);
        fs.SetReparsePoint(node, null!, "\\link", data);
        var result = Array.Empty<byte>();

        var status = fs.GetReparsePoint(node, null!, "\\link", ref result);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Equal(data, result);
    }

    [Fact]
    public void GetReparsePoint_OrdinaryFile_ReturnsNotAReparsePoint()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\file");
        var result = Array.Empty<byte>();

        var status = fs.GetReparsePoint(node, null!, "\\file", ref result);

        Assert.Equal(FileSystemBase.STATUS_NOT_A_REPARSE_POINT, status);
    }

    [Fact]
    public void GetReparsePointByName_ReparseNode_ReturnsItsData()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateDirectory(fs, "\\j");
        var data = MakeReparse(MountPointTag, 20);
        fs.SetReparsePoint(node, null!, "\\j", data);
        var result = Array.Empty<byte>();

        var status = fs.GetReparsePointByName("\\j", isDirectory: true, ref result);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Equal(data, result);
    }

    [Fact]
    public void GetReparsePointByName_OrdinaryDirectory_ReturnsNotAReparsePoint()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        CreateDirectory(fs, "\\dir");
        var result = Array.Empty<byte>();

        var status = fs.GetReparsePointByName("\\dir", isDirectory: true, ref result);

        Assert.Equal(FileSystemBase.STATUS_NOT_A_REPARSE_POINT, status);
    }

    [Fact]
    public void GetReparsePointByName_MissingPath_ReturnsNameNotFound()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var result = Array.Empty<byte>();

        var status = fs.GetReparsePointByName("\\nope", isDirectory: false, ref result);

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, status);
    }

    [Fact]
    public void DeleteReparsePoint_MatchingTag_RestoresOrdinaryNode()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\link");
        fs.SetReparsePoint(node, null!, "\\link", MakeReparse(SymlinkTag, 12));

        var status = fs.DeleteReparsePoint(node, null!, "\\link", MakeReparse(SymlinkTag, 0));

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Null(node.ReparseData);
        Assert.Equal(0U, node.FileInfo.ReparseTag);
        Assert.Equal(0U, node.FileInfo.FileAttributes & ReparseAttribute);
    }

    [Fact]
    public void DeleteReparsePoint_WrongTag_ReturnsTagMismatchAndKeepsLink()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\link");
        fs.SetReparsePoint(node, null!, "\\link", MakeReparse(SymlinkTag, 12));

        var status = fs.DeleteReparsePoint(node, null!, "\\link", MakeReparse(MountPointTag, 0));

        Assert.Equal(FileSystemBase.STATUS_IO_REPARSE_TAG_MISMATCH, status);
        Assert.NotNull(node.ReparseData);
    }

    [Fact]
    public void DeleteReparsePoint_OrdinaryFile_ReturnsNotAReparsePoint()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\file");

        var status = fs.DeleteReparsePoint(node, null!, "\\file", MakeReparse(SymlinkTag, 0));

        Assert.Equal(FileSystemBase.STATUS_NOT_A_REPARSE_POINT, status);
    }

    [Fact]
    public void GetSecurityByName_PathThroughJunction_ReturnsReparseWithJunctionIndex()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var junction = CreateDirectory(fs, "\\j");
        fs.SetReparsePoint(junction, null!, "\\j", MakeReparse(MountPointTag, 20));
        byte[] security = null!;

        var status = fs.GetSecurityByName("\\j\\sub\\file.txt", out var index, ref security);

        Assert.Equal(FileSystemBase.STATUS_REPARSE, status);
        Assert.Equal(1U, index); // last character of "\j"
    }

    [Fact]
    public void GetSecurityByName_PathThroughNestedJunction_IndexPointsAtTheJunction()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        CreateDirectory(fs, "\\a");
        var junction = CreateDirectory(fs, "\\a\\j");
        fs.SetReparsePoint(junction, null!, "\\a\\j", MakeReparse(MountPointTag, 20));
        byte[] security = null!;

        var status = fs.GetSecurityByName("\\a\\j\\x", out var index, ref security);

        Assert.Equal(FileSystemBase.STATUS_REPARSE, status);
        Assert.Equal(3U, index); // last character of "\a\j"
    }

    [Fact]
    public void GetSecurityByName_PathThroughFileSymlink_ReturnsReparse()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var link = CreateFile(fs, "\\s");
        fs.SetReparsePoint(link, null!, "\\s", MakeReparse(SymlinkTag, 12));
        byte[] security = null!;

        var status = fs.GetSecurityByName("\\s\\x", out _, ref security);

        Assert.Equal(FileSystemBase.STATUS_REPARSE, status);
    }

    [Fact]
    public void GetSecurityByName_LeafIsLink_ReturnsSuccessWithReparseAttribute()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var link = CreateFile(fs, "\\s");
        fs.SetReparsePoint(link, null!, "\\s", MakeReparse(SymlinkTag, 12));
        byte[] security = null!;

        var status = fs.GetSecurityByName("\\s", out var attributes, ref security);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.NotEqual(0U, attributes & ReparseAttribute);
    }

    [Fact]
    public void GetSecurityByName_MissingPathWithoutLinks_StillReportsNotFound()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        CreateDirectory(fs, "\\dir");
        byte[] security = null!;

        Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, fs.GetSecurityByName("\\dir\\x", out _, ref security));
        Assert.Equal(FileSystemBase.STATUS_OBJECT_PATH_NOT_FOUND, fs.GetSecurityByName("\\nope\\x", out _, ref security));
    }

    [Fact]
    public void Open_PathThroughJunction_ReturnsReparse()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var junction = CreateDirectory(fs, "\\j");
        fs.SetReparsePoint(junction, null!, "\\j", MakeReparse(MountPointTag, 20));

        var status = fs.Open("\\j\\x", 0, 0, out var node, out _, out _, out _);

        Assert.Equal(FileSystemBase.STATUS_REPARSE, status);
        Assert.Null(node);
    }

    [Fact]
    public void Open_LinkItself_OpensTheLinkNode()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var link = CreateFile(fs, "\\s");
        fs.SetReparsePoint(link, null!, "\\s", MakeReparse(SymlinkTag, 12));

        var status = fs.Open("\\s", 0, 0, out var node, out _, out var info, out _);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Same(link, node);
        Assert.Equal(SymlinkTag, info.ReparseTag);
    }

    [Fact]
    public void Create_AttributesContainReparseBit_BitIsNotStored()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");

        fs.Create("\\file", 0, 0, (uint)FileAttributes.Normal | ReparseAttribute, [], 0,
            out var node, out _, out var info, out _);

        Assert.Equal(0U, info.FileAttributes & ReparseAttribute);
        Assert.Null(((FileNode)node!).ReparseData);
    }

    [Fact]
    public void SetBasicInfo_ClearingReparseBitOnLink_KeepsTheBit()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\s");
        fs.SetReparsePoint(node, null!, "\\s", MakeReparse(SymlinkTag, 12));

        fs.SetBasicInfo(node, null!, (uint)FileAttributes.Normal, 0, 0, 0, 0, out var info);

        Assert.NotEqual(0U, info.FileAttributes & ReparseAttribute);
        Assert.Equal(SymlinkTag, info.ReparseTag);
    }

    [Fact]
    public void SetBasicInfo_SettingReparseBitOnOrdinaryFile_DoesNotStoreTheBit()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\file");

        fs.SetBasicInfo(node, null!, ReparseAttribute | (uint)FileAttributes.Hidden, 0, 0, 0, 0, out var info);

        Assert.Equal(0U, info.FileAttributes & ReparseAttribute);
        Assert.NotEqual(0U, info.FileAttributes & (uint)FileAttributes.Hidden);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Overwrite_OnLink_KeepsTheReparseBit(bool replaceAttributes)
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\s");
        fs.SetReparsePoint(node, null!, "\\s", MakeReparse(SymlinkTag, 12));

        fs.Overwrite(node, null!, (uint)FileAttributes.Archive, replaceAttributes, 0, out var info);

        Assert.NotEqual(0U, info.FileAttributes & ReparseAttribute);
    }

    [Fact]
    public void Rename_Link_KeepsTagAndData()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\old");
        var data = MakeReparse(SymlinkTag, 12);
        fs.SetReparsePoint(node, null!, "\\old", data);

        var status = fs.Rename(node, null!, "\\old", "\\new", replaceIfExists: false);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.True(fs.NodeMap.TryGet("\\new", out var renamed));
        Assert.Equal(data, renamed!.ReparseData);
        Assert.Equal(SymlinkTag, renamed.FileInfo.ReparseTag);
    }

    [Fact]
    public void GetDirInfoByName_Link_ReportsReparseTag()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Init(null!);
        fs.NodeMap.TryGet("\\", out var root);
        var link = CreateFile(fs, "\\s");
        fs.SetReparsePoint(link, null!, "\\s", MakeReparse(SymlinkTag, 12));

        var status = fs.GetDirInfoByName(root!, null!, "s", out _, out var info);

        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        Assert.Equal(SymlinkTag, info.ReparseTag);
        Assert.NotEqual(0U, info.FileAttributes & ReparseAttribute);
    }

    [Fact]
    public void Clone_NodeWithReparseData_CopiesItIndependently()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateFile(fs, "\\s");
        fs.SetReparsePoint(node, null!, "\\s", MakeReparse(SymlinkTag, 12));

        var clone = node.Clone();
        clone.ReparseData![^1] ^= 0xFF;

        Assert.NotSame(node.ReparseData, clone.ReparseData);
        Assert.NotEqual(node.ReparseData, clone.ReparseData);
        Assert.Equal(SymlinkTag, clone.FileInfo.ReparseTag);
    }

    [Fact]
    public void TryFindReparsePrefix_LinkIsTheLastComponent_IsNotReported()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateDirectory(fs, "\\j");
        fs.SetReparsePoint(node, null!, "\\j", MakeReparse(MountPointTag, 20));

        var found = fs.NodeMap.TryFindReparsePrefix("\\j", out var index);

        Assert.False(found);
        Assert.Equal(0U, index);
    }

    [Fact]
    public void TryFindReparsePrefix_PathWithoutLinks_ReturnsFalse()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        CreateDirectory(fs, "\\a");

        Assert.False(fs.NodeMap.TryFindReparsePrefix("\\a\\b\\c", out _));
    }

    [Fact]
    public void TryFindReparsePrefix_PrefixIsCaseInsensitive()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        var node = CreateDirectory(fs, "\\Junction");
        fs.SetReparsePoint(node, null!, "\\Junction", MakeReparse(MountPointTag, 20));

        Assert.True(fs.NodeMap.TryFindReparsePrefix("\\JUNCTION\\x", out var index));
        Assert.Equal(8U, index);
    }

    [Fact]
    public void TryReplaceContents_SourceHasLink_CopiesTheLink()
    {
        var source = new MemoryFileSystem(1024 * 1024, "Source");
        var link = CreateFile(source, "\\s");
        var data = MakeReparse(SymlinkTag, 12);
        source.SetReparsePoint(link, null!, "\\s", data);
        var target = new MemoryFileSystem(1024 * 1024, "Target");

        var replaced = target.TryReplaceContents(source.NodeMap, out _);

        Assert.True(replaced);
        Assert.True(target.NodeMap.TryGet("\\s", out var copy));
        Assert.Equal(data, copy!.ReparseData);
        Assert.NotSame(link, copy);
    }

    private static FileNode CreateFile(MemoryFileSystem fs, string path)
    {
        var status = fs.Create(path, 0, 0, (uint)FileAttributes.Normal, [], 0, out var node, out _, out _, out _);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        return (FileNode)node!;
    }

    private static FileNode CreateDirectory(MemoryFileSystem fs, string path)
    {
        var status = fs.Create(path, 0, 0, (uint)FileAttributes.Directory, [], 0, out var node, out _, out _, out _);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
        return (FileNode)node!;
    }

    private static byte[] MakeReparse(uint tag, int payloadLength)
    {
        var data = new byte[8 + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(data, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)payloadLength);
        for (var i = 0; i < payloadLength; i++)
        {
            data[8 + i] = (byte)(i + 1);
        }

        return data;
    }
}
