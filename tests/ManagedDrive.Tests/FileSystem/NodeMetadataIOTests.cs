using System.Text;

namespace ManagedDrive.Tests;

public sealed class NodeMetadataIOTests
{
    [Fact]
    public void WriteThenRead_FullFileMetadata_RoundTripsAllFields()
    {
        var node = new FileNode
        {
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Normal,
                AllocationSize = 4096,
                FileSize = 123,
                CreationTime = 100,
                LastAccessTime = 200,
                LastWriteTime = 300,
                ChangeTime = 400,
                IndexNumber = 500,
                HardLinks = 1,
            },
            FileSecurity = [1, 2, 3, 4, 5],
        };

        var metadata = RoundTrip("\\Folder\\File.txt", node);

        Assert.Equal("\\Folder\\File.txt", metadata.Path);
        Assert.Equal(node.FileInfo.FileAttributes, metadata.FileInfo.FileAttributes);
        Assert.Equal(node.FileInfo.AllocationSize, metadata.FileInfo.AllocationSize);
        Assert.Equal(node.FileInfo.FileSize, metadata.FileInfo.FileSize);
        Assert.Equal(node.FileInfo.CreationTime, metadata.FileInfo.CreationTime);
        Assert.Equal(node.FileInfo.LastAccessTime, metadata.FileInfo.LastAccessTime);
        Assert.Equal(node.FileInfo.LastWriteTime, metadata.FileInfo.LastWriteTime);
        Assert.Equal(node.FileInfo.ChangeTime, metadata.FileInfo.ChangeTime);
        Assert.Equal(node.FileInfo.IndexNumber, metadata.FileInfo.IndexNumber);
        Assert.Equal(node.FileInfo.HardLinks, metadata.FileInfo.HardLinks);
        Assert.Equal(node.FileSecurity, metadata.Security);
    }

    [Fact]
    public void WriteThenRead_NullSecurity_RoundTripsAsNull()
    {
        var node = new FileNode
        {
            FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
            FileSecurity = null,
        };

        var metadata = RoundTrip("\\Folder", node);

        Assert.Null(metadata.Security);
    }

    [Fact]
    public void WriteThenRead_EmptySecurityArray_RoundTripsAsNull()
    {
        var node = new FileNode
        {
            FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
            FileSecurity = [],
        };

        var metadata = RoundTrip("\\Folder", node);

        Assert.Null(metadata.Security);
    }

    [Fact]
    public void WriteThenRead_EmptyPath_RoundTrips()
    {
        var node = new FileNode { FileInfo = { FileAttributes = (uint)FileAttributes.Directory } };

        var metadata = RoundTrip(string.Empty, node);

        Assert.Equal(string.Empty, metadata.Path);
    }

    private static NodeMetadataIO.NodeMetadata RoundTrip(string path, FileNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            NodeMetadataIO.WriteMetadata(writer, path, node);
        }

        buffer.Position = 0;
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
        return NodeMetadataIO.ReadMetadata(reader);
    }
}
