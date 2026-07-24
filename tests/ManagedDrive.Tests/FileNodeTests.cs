namespace ManagedDrive.Tests;

public sealed class FileNodeTests
{
    [Fact]
    public void AlignToAllocationUnit_ExactMultiple_Unchanged()
    {
        var result = FileNode.AlignToAllocationUnit(512);
        Assert.Equal(512UL, result);
    }

    [Theory]
    [InlineData(1, 512)]
    [InlineData(511, 512)]
    [InlineData(513, 1024)]
    [InlineData(1023, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 1536)]
    public void AlignToAllocationUnit_RoundsUp(ulong input, ulong expected)
    {
        var result = FileNode.AlignToAllocationUnit(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AlignToAllocationUnit_Zero_ReturnsZero()
    {
        var result = FileNode.AlignToAllocationUnit(0);
        Assert.Equal(0UL, result);
    }

    [Fact]
    public void FilePath_DefaultIsEmpty()
    {
        var node = new FileNode();
        Assert.Equal(string.Empty, node.FilePath);
    }

    [Fact]
    public void IsDirectory_DirectoryAttribute_ReturnsTrue()
    {
        var node = new FileNode
        {
            FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
        };
        Assert.True(node.IsDirectory);
    }

    [Fact]
    public void IsDirectory_NormalAttribute_ReturnsFalse()
    {
        var node = new FileNode
        {
            FileInfo = { FileAttributes = (uint)FileAttributes.Normal },
        };
        Assert.False(node.IsDirectory);
    }

    [Fact]
    public void NewIndexNumber_ReturnsUniqueValues()
    {
        var a = FileNode.NewIndexNumber();
        var b = FileNode.NewIndexNumber();
        Assert.NotEqual(a, b);
        Assert.True(b > a);
    }

    [Fact]
    public void Clone_CopiesMetadataAndBuffers()
    {
        var original = new FileNode
        {
            FilePath = "\\file.txt",
            LeafName = "file.txt",
            FileInfo = { FileAttributes = (uint)FileAttributes.Normal, FileSize = 3 },
            FileData = [1, 2, 3],
            FileSecurity = [9, 8],
        };

        var clone = original.Clone();

        Assert.Equal(original.FilePath, clone.FilePath);
        Assert.Equal(original.LeafName, clone.LeafName);
        Assert.Equal(original.FileInfo.FileSize, clone.FileInfo.FileSize);
        Assert.Equal(original.FileData, clone.FileData);
        Assert.Equal(original.FileSecurity, clone.FileSecurity);
    }

    [Fact]
    public void Clone_ReturnsIndependentBuffers()
    {
        var original = new FileNode { FileData = [1, 2, 3], FileSecurity = [9, 8] };
        var clone = original.Clone();

        clone.FileData![0] = 99;
        clone.FileSecurity![0] = 77;

        Assert.Equal(1, original.FileData[0]);
        Assert.Equal(9, original.FileSecurity[0]);
    }
}