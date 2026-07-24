namespace ManagedDrive.Tests;

public sealed class ArchiveNodeMapWriterTests
{
    [Fact]
    public void WriteArchive_ReportsFinalProgressOfOne()
    {
        var nodeMap = BuildSourceNodeMap();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        var reports = new List<double>();

        try
        {
            ArchiveNodeMapWriter.WriteArchive(
                nodeMap,
                path,
                ArchiveExportFormat.Zip,
                ImageCompressionLevel.Fastest,
                new RecordingProgress(reports));

            Assert.NotEmpty(reports);
            Assert.Equal(1.0, reports[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ArchiveExportFormat.Zip)]
    [InlineData(ArchiveExportFormat.SevenZip)]
    public void WriteArchive_RoundTripsThroughArchiveNodeMapBuilder(ArchiveExportFormat format)
    {
        var nodeMap = BuildSourceNodeMap();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{Extension(format)}");

        try
        {
            ArchiveNodeMapWriter.WriteArchive(nodeMap, path, format, ImageCompressionLevel.Fastest);

            var restored = ArchiveNodeMapBuilder.BuildNodeMap(path);

            Assert.True(restored.TryGet("\\Folder\\File.txt", out var file));
            Assert.False(file!.IsDirectory);
            Assert.Equal("hello world"u8.ToArray(), file.FileData![..(int)file.FileInfo.FileSize]);

            Assert.True(restored.TryGet("\\Root.txt", out var rootFile));
            Assert.Equal("root content"u8.ToArray(), rootFile!.FileData![..(int)rootFile.FileInfo.FileSize]);

            Assert.True(restored.TryGet("\\EmptyFolder", out var emptyFolder));
            Assert.True(emptyFolder!.IsDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FileNodeMap BuildSourceNodeMap()
    {
        var nodeMap = new FileNodeMap();
        var now = (ulong)DateTimeOffset.UtcNow.ToFileTime();

        nodeMap.Add("\\", MakeDir(now));
        nodeMap.Add("\\Folder", MakeDir(now));
        nodeMap.Add("\\EmptyFolder", MakeDir(now));
        nodeMap.Add("\\Folder\\File.txt", MakeFile("hello world"u8.ToArray(), now));
        nodeMap.Add("\\Root.txt", MakeFile("root content"u8.ToArray(), now));

        return nodeMap;
    }

    private static string Extension(ArchiveExportFormat format) => format switch
    {
        ArchiveExportFormat.SevenZip => ".7z",
        _ => ".zip",
    };

    private static FileNode MakeDir(ulong timestamp) => new()
    {
        FileSecurity = FileNode.DefaultSecurityDescriptorBytes,
        FileInfo =
        {
            FileAttributes = (uint)FileAttributes.Directory,
            CreationTime = timestamp,
            LastAccessTime = timestamp,
            LastWriteTime = timestamp,
            ChangeTime = timestamp,
            IndexNumber = FileNode.NewIndexNumber(),
        },
    };

    private static FileNode MakeFile(byte[] content, ulong timestamp)
    {
        var size = (ulong)content.Length;
        var allocationSize = FileNode.AlignToAllocationUnit(size);
        var data = new byte[allocationSize];
        content.CopyTo(data, 0);

        return new()
        {
            FileData = data,
            FileSecurity = FileNode.DefaultSecurityDescriptorBytes,
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Normal,
                FileSize = size,
                AllocationSize = allocationSize,
                CreationTime = timestamp,
                LastAccessTime = timestamp,
                LastWriteTime = timestamp,
                ChangeTime = timestamp,
                IndexNumber = FileNode.NewIndexNumber(),
            },
        };
    }
}