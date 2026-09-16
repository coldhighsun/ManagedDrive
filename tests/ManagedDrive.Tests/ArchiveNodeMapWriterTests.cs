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

    [Fact]
    public void WriteArchive_DoesNotTruncateFilesLargerThanInt32MaxValue()
    {
        // Regression test for a bug where the entry size was cast to `int` before being handed to
        // FileContent.AsReadOnlyStream, silently truncating/corrupting the export for any file at
        // or above 2 GiB. Use ImageCompressionLevel.None (stored, no deflate work) to keep a
        // 2 GiB-plus export fast enough for a unit test.
        const long size = (long)int.MaxValue + 4096;
        var alignedSize = FileNode.AlignToAllocationUnit((ulong)size);

        var nodeMap = new FileNodeMap();
        var now = (ulong)DateTimeOffset.UtcNow.ToFileTime();
        nodeMap.Add("\\", new FileNode
        {
            FileSecurity = FileNode.DefaultSecurityDescriptorBytes,
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Directory,
                CreationTime = now,
                LastAccessTime = now,
                LastWriteTime = now,
                ChangeTime = now,
                IndexNumber = FileNode.NewIndexNumber(),
            },
        });
        nodeMap.Add("\\Big.bin", new FileNode
        {
            FileData = FileContent.CreateZeroed(alignedSize),
            FileSecurity = FileNode.DefaultSecurityDescriptorBytes,
            FileInfo =
            {
                FileAttributes = (uint)FileAttributes.Normal,
                FileSize = (ulong)size,
                AllocationSize = alignedSize,
                CreationTime = now,
                LastAccessTime = now,
                LastWriteTime = now,
                ChangeTime = now,
                IndexNumber = FileNode.NewIndexNumber(),
            },
        });

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        try
        {
            ArchiveNodeMapWriter.WriteArchive(nodeMap, path, ArchiveExportFormat.Zip, ImageCompressionLevel.None);

            var restored = ArchiveNodeMapBuilder.BuildNodeMap(path);
            Assert.True(restored.TryGet("\\Big.bin", out var restoredFile));
            Assert.Equal(size, (long)restoredFile!.FileInfo.FileSize);
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
            Assert.Equal("hello world"u8.ToArray(), file.FileData!.ToArray((long)file.FileInfo.FileSize));

            Assert.True(restored.TryGet("\\Root.txt", out var rootFile));
            Assert.Equal("root content"u8.ToArray(), rootFile!.FileData!.ToArray((long)rootFile.FileInfo.FileSize));

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

        return new()
        {
            FileData = FileContent.FromSpan(content, allocationSize),
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
