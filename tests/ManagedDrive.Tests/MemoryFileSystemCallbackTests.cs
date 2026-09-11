using System.Runtime.InteropServices;

namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemCallbackTests
{
    [Fact]
    public void Create_NewFile_AddsNodeAndMarksDirty()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");

        var status = fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 4096,
            out var fileNode, out _, out var fileInfo, out _);

        Assert.Equal(0, status);
        Assert.True(fs.IsDirty);
        Assert.True(fs.NodeMap.TryGet("\\file.bin", out var node));
        Assert.Same(node, fileNode);
        Assert.Equal(4096u, fileInfo.AllocationSize);
    }

    [Fact]
    public void Create_DuplicateName_ReturnsNameCollision()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0, out _, out _, out _, out _);

        var status = fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out _, out _, out _, out _);

        Assert.Equal(unchecked((int)0xC0000035), status); // STATUS_OBJECT_NAME_COLLISION
    }

    [Fact]
    public void Create_ExceedingCapacity_ReturnsDiskFull()
    {
        var fs = new MemoryFileSystem(1024, "Label");

        var status = fs.Create("\\big.bin", 0, 0, (uint)FileAttributes.Normal, [], 4096,
            out _, out _, out _, out _);

        Assert.Equal(unchecked((int)0xC000007F), status); // STATUS_DISK_FULL
    }

    [Fact]
    public void Create_OnReadOnlyFileSystem_ReturnsWriteProtected()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label", readOnly: true);

        var status = fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out _, out _, out _, out _);

        Assert.Equal(unchecked((int)0xC00000A2), status); // STATUS_MEDIA_WRITE_PROTECTED
        Assert.False(fs.IsDirty);
    }

    [Fact]
    public void WriteThenRead_RoundTripsBytesAndBumpsContentVersion()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        var node = (FileNode)fileNode!;
        var versionBefore = node.ContentVersion;
        var data = new byte[] { 1, 2, 3, 4, 5 };

        WriteBytes(fs, fileNode!, data, offset: 0);

        Assert.True(node.ContentVersion > versionBefore);
        Assert.NotNull(fs.LastContentWriteAccess);
        Assert.Equal("\\file.bin", fs.LastContentWriteAccess!.Path);

        var readBack = ReadBytes(fs, fileNode!, data.Length, offset: 0);
        Assert.Equal(data, readBack);
        Assert.NotNull(fs.LastContentReadAccess);
        Assert.Equal(data.Length, fs.TotalBytesRead);
        Assert.Equal(data.Length, fs.TotalBytesWritten);
    }

    [Fact]
    public void Read_PastEndOfFile_ReturnsEndOfFile()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        WriteBytes(fs, fileNode!, [1, 2, 3], offset: 0);

        var ptr = Marshal.AllocHGlobal(16);
        try
        {
            var status = fs.Read(fileNode!, null!, ptr, offset: 100, length: 16, out var bytesTransferred);
            Assert.Equal(unchecked((int)0xC0000011), status); // STATUS_END_OF_FILE
            Assert.Equal(0u, bytesTransferred);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void Write_OnReadOnlyFileSystem_ReturnsWriteProtectedAndLeavesContentUnchanged()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label", readOnly: true);
        var node = new FileNode
        {
            FileInfo = { FileAttributes = (uint)FileAttributes.Normal, AllocationSize = 512 },
            FileData = FileContent.CreateZeroed(512),
        };
        fs.NodeMap.Add("\\file.bin", node);

        var status = fs.Write(node, null!, IntPtr.Zero, 0, 0, false, false, out var bytesTransferred, out _);

        Assert.Equal(unchecked((int)0xC00000A2), status); // STATUS_MEDIA_WRITE_PROTECTED
        Assert.Equal(0u, bytesTransferred);
    }

    [Fact]
    public void Rename_UpdatesNodeMapAndBumpsMetadataVersion()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\old.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        var node = (FileNode)fileNode!;
        var versionBefore = node.MetadataVersion;

        var status = fs.Rename(fileNode!, null!, "\\old.bin", "\\new.bin", replaceIfExists: false);

        Assert.Equal(0, status);
        Assert.True(node.MetadataVersion > versionBefore);
        Assert.False(fs.NodeMap.TryGet("\\old.bin", out _));
        Assert.True(fs.NodeMap.TryGet("\\new.bin", out var moved));
        Assert.Same(node, moved);
    }

    [Fact]
    public void Rename_TargetExistsWithoutReplace_ReturnsNameCollision()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\a.bin", 0, 0, (uint)FileAttributes.Normal, [], 0, out var fileNode, out _, out _, out _);
        fs.Create("\\b.bin", 0, 0, (uint)FileAttributes.Normal, [], 0, out _, out _, out _, out _);

        var status = fs.Rename(fileNode!, null!, "\\a.bin", "\\b.bin", replaceIfExists: false);

        Assert.Equal(unchecked((int)0xC0000035), status); // STATUS_OBJECT_NAME_COLLISION
    }

    [Fact]
    public void CanDelete_NonEmptyDirectory_ReturnsDirectoryNotEmpty()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\dir", 0, 0, (uint)FileAttributes.Directory, [], 0,
            out var dirNode, out _, out _, out _);
        fs.Create("\\dir\\child.bin", 0, 0, (uint)FileAttributes.Normal, [], 0, out _, out _, out _, out _);

        var status = fs.CanDelete(dirNode!, null!, "\\dir");

        Assert.Equal(unchecked((int)0xC0000101), status); // STATUS_DIRECTORY_NOT_EMPTY
    }

    [Fact]
    public void CanDelete_EmptyDirectory_ReturnsSuccess()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\dir", 0, 0, (uint)FileAttributes.Directory, [], 0,
            out var dirNode, out _, out _, out _);

        var status = fs.CanDelete(dirNode!, null!, "\\dir");

        Assert.Equal(0, status);
    }

    [Fact]
    public void Cleanup_WithDeleteFlag_RemovesNodeFromMap()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);

        fs.Cleanup(fileNode!, null!, "\\file.bin", MemoryFileSystem.CleanupDelete);

        Assert.False(fs.NodeMap.TryGet("\\file.bin", out _));
    }

    [Fact]
    public void Cleanup_WithSetLastWriteTimeFlag_BumpsMetadataVersion()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        var node = (FileNode)fileNode!;
        var versionBefore = node.MetadataVersion;

        fs.Cleanup(fileNode!, null!, "\\file.bin", MemoryFileSystem.CleanupSetLastWriteTime);

        Assert.True(node.MetadataVersion > versionBefore);
    }

    [Fact]
    public void SetBasicInfo_UpdatesAttributesAndBumpsMetadataVersion()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        var node = (FileNode)fileNode!;
        var versionBefore = node.MetadataVersion;

        var status = fs.SetBasicInfo(fileNode!, null!, (uint)FileAttributes.ReadOnly,
            0, 0, 0, 0, out var fileInfo);

        Assert.Equal(0, status);
        Assert.Equal((uint)FileAttributes.ReadOnly, fileInfo.FileAttributes);
        Assert.True(node.MetadataVersion > versionBefore);
        Assert.True(fs.IsDirty);
    }

    [Fact]
    public void Overwrite_ResetsContentAndBumpsContentVersion()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        WriteBytes(fs, fileNode!, [1, 2, 3, 4], offset: 0);
        var node = (FileNode)fileNode!;
        var versionBefore = node.ContentVersion;

        var status = fs.Overwrite(fileNode!, null!, (uint)FileAttributes.Normal, true, 0, out var fileInfo);

        Assert.Equal(0, status);
        Assert.Equal(0u, fileInfo.FileSize);
        Assert.True(node.ContentVersion > versionBefore);
    }

    [Fact]
    public void GetFileInfo_ReturnsCurrentNodeState()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 4096,
            out var fileNode, out _, out _, out _);

        var status = fs.GetFileInfo(fileNode!, null!, out var fileInfo);

        Assert.Equal(0, status);
        Assert.Equal(4096u, fileInfo.AllocationSize);
    }

    [Fact]
    public void Open_ExistingPath_ReturnsSameNode()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var created, out _, out _, out _);

        var status = fs.Open("\\file.bin", 0, 0, out var opened, out _, out _, out _);

        Assert.Equal(0, status);
        Assert.Same(created, opened);
    }

    [Fact]
    public void Open_MissingPath_ReturnsObjectNameNotFound()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");

        var status = fs.Open("\\missing.bin", 0, 0, out _, out _, out _, out _);

        Assert.Equal(unchecked((int)0xC0000034), status); // STATUS_OBJECT_NAME_NOT_FOUND
    }

    [Fact]
    public void ClearDirty_AfterMarkDirty_ResetsIsDirty()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0, out _, out _, out _, out _);
        Assert.True(fs.IsDirty);

        fs.ClearDirty();

        Assert.False(fs.IsDirty);
    }

    [Fact]
    public void ContentAccessed_FiresForReadsAndWrites()
    {
        var fs = new MemoryFileSystem(1024 * 1024, "Label");
        fs.Create("\\file.bin", 0, 0, (uint)FileAttributes.Normal, [], 0,
            out var fileNode, out _, out _, out _);
        var events = new List<bool>();
        fs.ContentAccessed += isWrite => events.Add(isWrite);

        WriteBytes(fs, fileNode!, [1, 2, 3], offset: 0);
        ReadBytes(fs, fileNode!, 3, offset: 0);

        Assert.Contains(true, events);
        Assert.Contains(false, events);
    }

    private static void WriteBytes(MemoryFileSystem fs, object fileNode, byte[] data, ulong offset)
    {
        var ptr = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            var status = fs.Write(fileNode, null!, ptr, offset, (uint)data.Length, false, false,
                out var bytesTransferred, out _);
            Assert.Equal(0, status);
            Assert.Equal((uint)data.Length, bytesTransferred);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static byte[] ReadBytes(MemoryFileSystem fs, object fileNode, int length, ulong offset)
    {
        var ptr = Marshal.AllocHGlobal(Math.Max(length, 1));
        try
        {
            var status = fs.Read(fileNode, null!, ptr, offset, (uint)length, out var bytesTransferred);
            Assert.Equal(0, status);
            var result = new byte[bytesTransferred];
            Marshal.Copy(ptr, result, 0, (int)bytesTransferred);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
